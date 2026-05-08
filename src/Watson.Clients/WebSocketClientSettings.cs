namespace Watson.Clients
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.WebSockets;

    /// <summary>
    /// Settings used to configure a Watson WebSocket client connection.
    /// </summary>
    public class WebSocketClientSettings
    {
        /// <summary>
        /// Request headers to apply before connecting.
        /// </summary>
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Requested WebSocket subprotocols.
        /// </summary>
        public IList<string> RequestedSubprotocols { get; } = new List<string>();

        /// <summary>
        /// Cookies to apply before connecting.
        /// </summary>
        public CookieContainer Cookies { get; set; } = new CookieContainer();

        /// <summary>
        /// Keepalive interval to apply to the underlying client socket.
        /// </summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether invalid or otherwise untrusted TLS certificates should be accepted.
        /// The default is <c>false</c>.
        /// </summary>
        public bool AcceptInvalidCertificates { get; set; } = false;

        /// <summary>
        /// Optional client identifier to send in a request header when connecting.
        /// </summary>
        public Guid? ClientGuid { get; set; } = null;

        /// <summary>
        /// Request-header name used when sending <see cref="ClientGuid"/>.
        /// </summary>
        public string ClientGuidHeaderName { get; set; } = "x-guid";

        /// <summary>
        /// Timeout to use for graceful close handshakes.
        /// </summary>
        public int CloseHandshakeTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Optional callback used to customize <see cref="ClientWebSocketOptions"/> before connecting.
        /// </summary>
        public Action<ClientWebSocketOptions> ConfigureOptions { get; set; } = null;
    }
}
