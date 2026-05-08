namespace Watson.Clients
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Security;
    using System.Net.WebSockets;
    using System.Security.Cryptography.X509Certificates;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Async-first Watson WebSocket client with whole-message receive semantics.
    /// </summary>
    public class WatsonWebSocketClient : IDisposable
    {
        private readonly Uri _ServerUri;
        private readonly WebSocketClientSettings _Settings;
        private readonly SemaphoreSlim _SendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _StateLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _Lifetime = new CancellationTokenSource();
        private ClientWebSocket _Socket = null;
        private int _ReceiveState = 0;
        private int _Disposed = 0;
        private int _CloseStateSet = 0;
        private WebSocketCloseStatus? _CloseStatus = null;
        private string _CloseStatusDescription = null;
        private WebSocketState _TrackedState = WebSocketState.None;
        private bool _HasTrackedState = false;

        /// <summary>
        /// Instantiate the client using a websocket URI.
        /// </summary>
        /// <param name="serverUri">WebSocket server URI.</param>
        /// <param name="settings">Optional settings.</param>
        public WatsonWebSocketClient(Uri serverUri, WebSocketClientSettings settings = null)
        {
            if (serverUri == null) throw new ArgumentNullException(nameof(serverUri));
            if (!String.Equals(serverUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(serverUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The URI scheme must be ws or wss.", nameof(serverUri));
            }

            _ServerUri = serverUri;
            _Settings = settings ?? new WebSocketClientSettings();
        }

        /// <summary>
        /// Instantiate the client using host, port, and TLS convenience arguments.
        /// </summary>
        /// <param name="hostname">Hostname or IP address.</param>
        /// <param name="port">TCP port.</param>
        /// <param name="ssl">Whether to use TLS.</param>
        /// <param name="path">WebSocket path.</param>
        /// <param name="settings">Optional settings.</param>
        public WatsonWebSocketClient(string hostname, int port, bool ssl = false, string path = "/", WebSocketClientSettings settings = null)
            : this(BuildUri(hostname, port, ssl, path), settings)
        {
        }

        /// <summary>
        /// Destination server URI.
        /// </summary>
        public Uri ServerUri => _ServerUri;

        /// <summary>
        /// Indicates whether the client is connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                WebSocketState state = State;
                return state == WebSocketState.Open || state == WebSocketState.CloseReceived || state == WebSocketState.CloseSent;
            }
        }

        /// <summary>
        /// Current socket state.
        /// </summary>
        public WebSocketState State
        {
            get
            {
                if (_HasTrackedState) return _TrackedState;

                try
                {
                    return _Socket?.State ?? WebSocketState.None;
                }
                catch (ObjectDisposedException)
                {
                    return WebSocketState.Closed;
                }
            }
        }

        /// <summary>
        /// Final or observed close status.
        /// </summary>
        public WebSocketCloseStatus? CloseStatus => _CloseStatus;

        /// <summary>
        /// Final or observed close description.
        /// </summary>
        public string CloseStatusDescription => _CloseStatusDescription;

        /// <summary>
        /// Negotiated subprotocol, if any.
        /// </summary>
        public string Subprotocol
        {
            get
            {
                try
                {
                    return _Socket?.SubProtocol;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Raw underlying client socket. This escape hatch is intended for advanced scenarios.
        /// Mixing raw receives with Watson-managed receive APIs on the same connection is unsupported.
        /// </summary>
        public ClientWebSocket RawSocket => _Socket;

        /// <summary>
        /// Connection statistics.
        /// </summary>
        public WebSocketClientStatistics Statistics { get; } = new WebSocketClientStatistics();

        /// <summary>
        /// Connect to the configured WebSocket server.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task ConnectAsync(CancellationToken token = default)
        {
            ThrowIfDisposed();

            await _StateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (IsConnected) throw new InvalidOperationException("The WebSocket client is already connected.");

                ResetConnectionState();
                ClientWebSocket socket = BuildSocket();

#if NETFRAMEWORK
                RemoteCertificateValidationCallback previousCallback = null;
                if (_Settings.AcceptInvalidCertificates)
                {
                    previousCallback = ServicePointManager.ServerCertificateValidationCallback;
                    ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                }

                try
                {
                    await socket.ConnectAsync(_ServerUri, token).ConfigureAwait(false);
                }
                finally
                {
                    if (_Settings.AcceptInvalidCertificates)
                    {
                        ServicePointManager.ServerCertificateValidationCallback = previousCallback;
                    }
                }
#else
                await socket.ConnectAsync(_ServerUri, token).ConfigureAwait(false);
#endif

                _Socket = socket;
            }
            finally
            {
                _StateLock.Release();
            }
        }

        /// <summary>
        /// Receive a single whole websocket message, or <c>null</c> when the connection closes.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Message or <c>null</c>.</returns>
        public async Task<WebSocketMessage> ReceiveAsync(CancellationToken token = default)
        {
            EnsureManagedReceiveAllowed();

            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _Lifetime.Token))
                {
                    return await ReceiveInternalAsync(linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _ReceiveState, 0);
            }
        }

        /// <summary>
        /// Read whole websocket messages until the connection closes.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Async sequence of messages.</returns>
        public async IAsyncEnumerable<WebSocketMessage> ReadMessagesAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            EnsureManagedReceiveAllowed();

            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _Lifetime.Token))
                {
                    while (!linked.Token.IsCancellationRequested)
                    {
                        WebSocketMessage message = await ReceiveInternalAsync(linked.Token).ConfigureAwait(false);
                        if (message == null) yield break;
                        yield return message;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _ReceiveState, 0);
            }
        }

        /// <summary>
        /// Send a UTF-8 text message.
        /// </summary>
        /// <param name="data">Text payload.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task SendTextAsync(string data, CancellationToken token = default)
        {
            byte[] bytes = data == null ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(data);
            return SendAsync(WebSocketMessageType.Text, new ArraySegment<byte>(bytes), token);
        }

        /// <summary>
        /// Send a binary message.
        /// </summary>
        /// <param name="data">Payload bytes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task SendBinaryAsync(byte[] data, CancellationToken token = default)
        {
            return SendAsync(WebSocketMessageType.Binary, new ArraySegment<byte>(data ?? Array.Empty<byte>()), token);
        }

        /// <summary>
        /// Send a binary message.
        /// </summary>
        /// <param name="data">Payload bytes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task SendBinaryAsync(ArraySegment<byte> data, CancellationToken token = default)
        {
            return SendAsync(WebSocketMessageType.Binary, data, token);
        }

        /// <summary>
        /// Close the connection gracefully.
        /// </summary>
        /// <param name="status">Close status.</param>
        /// <param name="reason">Close description.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken token = default)
        {
            ThrowIfDisposed();

            await _StateLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_Socket == null)
                {
                    SetCloseState(status, reason);
                    SetTrackedState(WebSocketState.Closed);
                    return;
                }

                SetCloseState(status, reason);

                if (_Socket.State == WebSocketState.Closed || _Socket.State == WebSocketState.Aborted)
                {
                    SetTrackedState(WebSocketState.Closed);
                    return;
                }

                SetTrackedState(WebSocketState.CloseSent);

                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _Lifetime.Token))
                {
                    linked.CancelAfter(_Settings.CloseHandshakeTimeoutMs < 1000 ? 1000 : _Settings.CloseHandshakeTimeoutMs);

                    try
                    {
                        await _Socket.CloseAsync(status, reason, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _Socket.Abort();
                    }
                    catch (WebSocketException)
                    {
                        _Socket.Abort();
                    }
                }

                SetTrackedState(WebSocketState.Closed);
            }
            finally
            {
                _StateLock.Release();
            }
        }

        /// <summary>
        /// Dispose the client and underlying socket state.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 1) return;

            try
            {
                _Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            CaptureStateFromSocket();

            if (_Socket != null)
            {
                try
                {
                    _Socket.Dispose();
                }
                catch (Exception)
                {
                }

                _Socket = null;
            }

            _Lifetime.Dispose();
            _SendLock.Dispose();
            _StateLock.Dispose();
            SetTrackedState(WebSocketState.Closed);
        }

        private static Uri BuildUri(string hostname, int port, bool ssl, string path)
        {
            if (String.IsNullOrWhiteSpace(hostname)) throw new ArgumentNullException(nameof(hostname));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            string normalizedPath = String.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            if (!normalizedPath.StartsWith("/")) normalizedPath = "/" + normalizedPath;

            UriBuilder builder = new UriBuilder
            {
                Scheme = ssl ? "wss" : "ws",
                Host = hostname.Trim(),
                Port = port,
                Path = normalizedPath
            };

            return builder.Uri;
        }

        private ClientWebSocket BuildSocket()
        {
            ClientWebSocket socket = new ClientWebSocket();
            socket.Options.Cookies = _Settings.Cookies ?? new CookieContainer();
            socket.Options.KeepAliveInterval = _Settings.KeepAliveInterval;

            if (_Settings.Headers != null)
            {
                foreach (KeyValuePair<string, string> header in _Settings.Headers)
                {
                    if (String.IsNullOrWhiteSpace(header.Key)) continue;
                    socket.Options.SetRequestHeader(header.Key, header.Value ?? String.Empty);
                }
            }

            if (_Settings.RequestedSubprotocols != null)
            {
                for (int i = 0; i < _Settings.RequestedSubprotocols.Count; i++)
                {
                    string requested = _Settings.RequestedSubprotocols[i];
                    if (String.IsNullOrWhiteSpace(requested)) continue;
                    socket.Options.AddSubProtocol(requested.Trim());
                }
            }

            if (_Settings.ClientGuid.HasValue && _Settings.ClientGuid.Value != Guid.Empty)
            {
                string headerName = String.IsNullOrWhiteSpace(_Settings.ClientGuidHeaderName) ? "x-guid" : _Settings.ClientGuidHeaderName.Trim();
                socket.Options.SetRequestHeader(headerName, _Settings.ClientGuid.Value.ToString());
            }

#if !NETFRAMEWORK
            if (_Settings.AcceptInvalidCertificates)
            {
                socket.Options.RemoteCertificateValidationCallback = AcceptCertificate;
            }
#endif

            _Settings.ConfigureOptions?.Invoke(socket.Options);
            return socket;
        }

        private static bool AcceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private void ResetConnectionState()
        {
            try
            {
                _Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_Socket != null)
            {
                try
                {
                    _Socket.Dispose();
                }
                catch (Exception)
                {
                }

                _Socket = null;
            }

            _Lifetime.Dispose();
            _Lifetime = new CancellationTokenSource();
            _CloseStatus = null;
            _CloseStatusDescription = null;
            _TrackedState = WebSocketState.None;
            _HasTrackedState = false;
            _CloseStateSet = 0;
            Interlocked.Exchange(ref _ReceiveState, 0);
        }

        private void EnsureManagedReceiveAllowed()
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                throw new InvalidOperationException("The WebSocket client is not connected.");
            }

            if (Interlocked.CompareExchange(ref _ReceiveState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Only one Watson-managed receive operation may be active per client.");
            }
        }

        private async Task SendAsync(WebSocketMessageType messageType, ArraySegment<byte> data, CancellationToken token)
        {
            ThrowIfDisposed();
            if (_Socket == null || !IsConnected) throw new IOException("The WebSocket client is not connected.");

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _Lifetime.Token))
            {
                await _SendLock.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await _Socket.SendAsync(data, messageType, true, linked.Token).ConfigureAwait(false);
                    Statistics.IncrementSent(data.Count);
                }
                finally
                {
                    _SendLock.Release();
                }
            }
        }

        private async Task<WebSocketMessage> ReceiveInternalAsync(CancellationToken token)
        {
            if (_Socket == null || !IsConnected) return null;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    while (true)
                    {
                        WebSocketReceiveResult result = await _Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            SetCloseState(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription);

                            if (_Socket.State == WebSocketState.CloseReceived || _Socket.State == WebSocketState.Open)
                            {
                                try
                                {
                                    await _Socket.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, token).ConfigureAwait(false);
                                }
                                catch (Exception)
                                {
                                }
                            }

                            SetTrackedState(WebSocketState.Closed);
                            return null;
                        }

                        if (result.Count > 0)
                        {
                            await stream.WriteAsync(buffer, 0, result.Count, token).ConfigureAwait(false);
                        }

                        if (result.EndOfMessage)
                        {
                            byte[] payload = stream.ToArray();
                            Statistics.IncrementReceived(payload.Length);
                            return new WebSocketMessage(result.MessageType, payload);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void CaptureStateFromSocket()
        {
            if (_Socket == null) return;

            try
            {
                if (_Socket.CloseStatus.HasValue)
                {
                    SetCloseState(_Socket.CloseStatus.Value, _Socket.CloseStatusDescription);
                }
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                SetTrackedState(_Socket.State);
            }
            catch (ObjectDisposedException)
            {
                SetTrackedState(WebSocketState.Closed);
            }
        }

        private void SetCloseState(WebSocketCloseStatus? status, string description)
        {
            if (!status.HasValue) return;
            if (Interlocked.CompareExchange(ref _CloseStateSet, 1, 0) != 0) return;

            _CloseStatus = status;
            _CloseStatusDescription = description;
        }

        private void SetTrackedState(WebSocketState state)
        {
            _TrackedState = state;
            _HasTrackedState = true;
        }

        private void ThrowIfDisposed()
        {
            if (_Disposed == 1) throw new ObjectDisposedException(nameof(WatsonWebSocketClient));
        }
    }
}
