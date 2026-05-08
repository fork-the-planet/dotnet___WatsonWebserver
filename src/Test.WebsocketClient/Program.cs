namespace Test.WebsocketClient
{
    using System;
    using System.Collections.Generic;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Watson.Clients;

    internal static class Program
    {
        private static readonly object _ConsoleSync = new object();
        private static WatsonWebSocketClient _Client = null;
        private static CancellationTokenSource _ReceiveCts = null;
        private static Task _ReceiveTask = null;
        private static string _Uri = "ws://127.0.0.1:8181/ws/echo";
        private static readonly Dictionary<string, string> _Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _Subprotocols = new List<string>();
        private static bool _AcceptInvalidCertificates = false;
        private static Guid? _ClientGuid = null;

        private static async Task Main(string[] args)
        {
            AttachLifecycleHandlers();
            PrintBanner();

            bool run = true;
            while (run)
            {
                Console.Write("ws-client> ");
                string input = Console.ReadLine();
                string command = (input ?? String.Empty).Trim().ToLowerInvariant();

                switch (command)
                {
                    case "?":
                    case "help":
                        PrintMenu();
                        break;
                    case "preset":
                        ChoosePreset();
                        break;
                    case "uri":
                        SetCustomUri();
                        break;
                    case "headers":
                        ManageHeaders();
                        break;
                    case "subprotocols":
                    case "subproto":
                        ManageSubprotocols();
                        break;
                    case "tls":
                    case "certs":
                        ManageTlsValidation();
                        break;
                    case "guid":
                        ManageClientGuid();
                        break;
                    case "connect":
                        await ConnectAsync().ConfigureAwait(false);
                        break;
                    case "disconnect":
                        await DisconnectAsync().ConfigureAwait(false);
                        break;
                    case "text":
                        await SendTextAsync().ConfigureAwait(false);
                        break;
                    case "textn":
                    case "burst":
                        await SendTextBurstAsync().ConfigureAwait(false);
                        break;
                    case "binary":
                        await SendBinaryAsync().ConfigureAwait(false);
                        break;
                    case "close":
                    case "closewith":
                        await CloseWithReasonAsync().ConfigureAwait(false);
                        break;
                    case "state":
                        PrintState();
                        break;
                    case "clear":
                    case "cls":
                        Console.Clear();
                        break;
                    case "quit":
                    case "q":
                    case "exit":
                        run = false;
                        break;
                    default:
                        if (!String.IsNullOrWhiteSpace(command))
                        {
                            WriteLine("Unknown command. Use ? for help.");
                        }
                        break;
                }
            }

            await DisconnectAsync().ConfigureAwait(false);
        }

        private static void AttachLifecycleHandlers()
        {
            Console.CancelKeyPress += (sender, args) =>
            {
                args.Cancel = false;
                WriteLine("[event] Ctrl+C received, disconnecting and exiting");
                try
                {
                    DisconnectAsync().GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    WriteLine("[event error] " + e.Message);
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                WriteLine("[event] ProcessExit");
                try
                {
                    DisconnectAsync().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Exception exception = args.ExceptionObject as Exception;
                WriteLine("[event] UnhandledException " + (exception?.Message ?? "(non-exception)"));
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                WriteLine("[event] UnobservedTaskException " + args.Exception.GetBaseException().Message);
                args.SetObserved();
            };
        }

        private static void PrintBanner()
        {
            WriteLine("Watson websocket sample client");
            WriteLine("Implementation: WatsonWebSocketClient from Watson.Clients");
            WriteLine("Default URI: " + _Uri);
            WriteLine("");
            PrintMenu();
        }

        private static void PrintMenu()
        {
            WriteLine("Commands:");
            WriteLine("  ? / help    show commands");
            WriteLine("  preset      choose a predefined endpoint");
            WriteLine("  uri         set a custom websocket URI");
            WriteLine("  headers     manage request headers");
            WriteLine("  subproto    manage requested subprotocols");
            WriteLine("  tls         toggle invalid-certificate acceptance");
            WriteLine("  guid        set or clear the client-supplied GUID header");
            WriteLine("  connect     connect to the current URI");
            WriteLine("  disconnect  close the active connection and tear down local state");
            WriteLine("  text        send a UTF-8 text message");
            WriteLine("  textn       send N UTF-8 text messages");
            WriteLine("  binary      send a UTF-8 string as binary bytes");
            WriteLine("  close       close with an explicit status and reason");
            WriteLine("  state       print connection state");
            WriteLine("  clear       clear the screen");
            WriteLine("  quit        exit");
            WriteLine("");
        }

        private static void ChoosePreset()
        {
            WriteLine("Presets:");
            WriteLine("  1  ws://127.0.0.1:8181/ws/echo");
            WriteLine("  2  ws://127.0.0.1:8181/ws/time");
            WriteLine("  3  ws://127.0.0.1:8181/ws/upper");
            WriteLine("  4  ws://127.0.0.1:8181/ws/inspect?name=alice");
            WriteLine("  5  ws://127.0.0.1:8181/ws/broadcast/general");
            Console.Write("Preset> ");
            string preset = (Console.ReadLine() ?? String.Empty).Trim();

            switch (preset)
            {
                case "1":
                    _Uri = "ws://127.0.0.1:8181/ws/echo";
                    break;
                case "2":
                    _Uri = "ws://127.0.0.1:8181/ws/time";
                    break;
                case "3":
                    _Uri = "ws://127.0.0.1:8181/ws/upper";
                    break;
                case "4":
                    _Uri = "ws://127.0.0.1:8181/ws/inspect?name=alice";
                    break;
                case "5":
                    _Uri = "ws://127.0.0.1:8181/ws/broadcast/general";
                    break;
                default:
                    WriteLine("Unknown preset.");
                    return;
            }

            WriteLine("Current URI: " + _Uri);
        }

        private static void SetCustomUri()
        {
            Console.Write("WebSocket URI> ");
            string value = (Console.ReadLine() ?? String.Empty).Trim();
            if (String.IsNullOrWhiteSpace(value))
            {
                WriteLine("URI unchanged.");
                return;
            }

            _Uri = value;
            WriteLine("Current URI: " + _Uri);
        }

        private static async Task ConnectAsync()
        {
            if (_Client != null && _Client.IsConnected)
            {
                WriteLine("Already connected.");
                return;
            }

            await DisconnectAsync().ConfigureAwait(false);

            _ReceiveCts = new CancellationTokenSource();
            _Client = BuildClient();

            try
            {
                await _Client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                _ReceiveTask = Task.Run(() => ReceiveLoopAsync(_Client, _ReceiveCts.Token));
                WriteLine("Connected to " + _Uri);

                if (!String.IsNullOrWhiteSpace(_Client.Subprotocol))
                {
                    WriteLine("Negotiated subprotocol: " + _Client.Subprotocol);
                }
            }
            catch (Exception e)
            {
                WriteLine("Connect failed: " + e.Message);
                await DisconnectAsync().ConfigureAwait(false);
            }
        }

        private static async Task DisconnectAsync()
        {
            WatsonWebSocketClient client = _Client;
            CancellationTokenSource receiveCts = _ReceiveCts;
            Task receiveTask = _ReceiveTask;

            _Client = null;
            _ReceiveCts = null;
            _ReceiveTask = null;

            if (receiveCts != null)
            {
                try
                {
                    receiveCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (client != null)
            {
                try
                {
                    if (client.IsConnected)
                    {
                        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    client.Dispose();
                }
            }

            if (receiveTask != null)
            {
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            if (receiveCts != null)
            {
                receiveCts.Dispose();
            }
        }

        private static async Task SendTextAsync()
        {
            if (!EnsureConnected()) return;

            Console.Write("Text payload> ");
            string payload = Console.ReadLine() ?? String.Empty;
            await _Client.SendTextAsync(payload, CancellationToken.None).ConfigureAwait(false);
            WriteLine("[sent text] " + payload);
        }

        private static async Task SendBinaryAsync()
        {
            if (!EnsureConnected()) return;

            Console.Write("Binary payload (UTF-8 source text)> ");
            string payload = Console.ReadLine() ?? String.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await _Client.SendBinaryAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            WriteLine("[sent binary] " + bytes.Length + " bytes");
        }

        private static async Task SendTextBurstAsync()
        {
            if (!EnsureConnected()) return;

            Console.Write("Message count> ");
            string countInput = (Console.ReadLine() ?? String.Empty).Trim();
            if (!Int32.TryParse(countInput, out int count) || count < 1)
            {
                WriteLine("Invalid count.");
                return;
            }

            Console.Write("Text payload template [hello]> ");
            string payload = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(payload)) payload = "hello";

            Console.Write("Append index suffix? [Y/n]> ");
            string suffixChoice = (Console.ReadLine() ?? String.Empty).Trim();
            bool appendIndex = !suffixChoice.Equals("n", StringComparison.OrdinalIgnoreCase)
                && !suffixChoice.Equals("no", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string outbound = appendIndex ? payload + " #" + (i + 1) : payload;
                await _Client.SendTextAsync(outbound, CancellationToken.None).ConfigureAwait(false);
            }

            WriteLine("[sent text burst] " + count + " messages");
        }

        private static async Task CloseWithReasonAsync()
        {
            if (!EnsureConnected()) return;

            Console.Write("Close status [NormalClosure]> ");
            string statusInput = (Console.ReadLine() ?? String.Empty).Trim();
            if (String.IsNullOrWhiteSpace(statusInput)) statusInput = nameof(WebSocketCloseStatus.NormalClosure);

            if (!Enum.TryParse(statusInput, true, out WebSocketCloseStatus closeStatus))
            {
                WriteLine("Invalid close status.");
                return;
            }

            Console.Write("Close reason [client close]> ");
            string reason = Console.ReadLine();
            if (String.IsNullOrWhiteSpace(reason)) reason = "client close";

            await _Client.CloseAsync(closeStatus, reason, CancellationToken.None).ConfigureAwait(false);
            WriteLine("[close sent] " + closeStatus + " " + reason);
        }

        private static void PrintState()
        {
            if (_Client == null)
            {
                WriteLine("Client: (null)");
                PrintConfigurationState();
                return;
            }

            WebSocketClientStatistics stats = _Client.Statistics.Snapshot();

            WriteLine("Socket state: " + _Client.State);
            WriteLine("Close status: " + (_Client.CloseStatus?.ToString() ?? "(null)"));
            WriteLine("Close description: " + (_Client.CloseStatusDescription ?? "(null)"));
            WriteLine("Subprotocol: " + (_Client.Subprotocol ?? "(null)"));
            WriteLine("Raw socket state: " + (_Client.RawSocket?.State.ToString() ?? "(null)"));
            WriteLine("Messages sent: " + stats.MessagesSent);
            WriteLine("Messages received: " + stats.MessagesReceived);
            WriteLine("Bytes sent: " + stats.BytesSent);
            WriteLine("Bytes received: " + stats.BytesReceived);
            PrintConfigurationState();
        }

        private static async Task ReceiveLoopAsync(WatsonWebSocketClient client, CancellationToken token)
        {
            try
            {
                await foreach (WebSocketMessage message in client.ReadMessagesAsync(token).ConfigureAwait(false))
                {
                    if (message.MessageType == WebSocketMessageType.Text)
                    {
                        WriteLine("[recv text] " + message.Text);
                    }
                    else
                    {
                        WriteLine("[recv binary] " + BitConverter.ToString(message.Data));
                    }
                }

                WriteLine("[recv close] " + (client.CloseStatus?.ToString() ?? "(null)") + " " + (client.CloseStatusDescription ?? String.Empty));
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                WriteLine("[recv error] " + e.Message);
            }
        }

        private static bool EnsureConnected()
        {
            if (_Client == null || !_Client.IsConnected)
            {
                WriteLine("Not connected.");
                return false;
            }

            return true;
        }

        private static void ManageHeaders()
        {
            WriteLine("Headers:");
            if (_Headers.Count < 1)
            {
                WriteLine("  (none)");
            }
            else
            {
                foreach (KeyValuePair<string, string> header in _Headers)
                {
                    WriteLine("  " + header.Key + ": " + header.Value);
                }
            }

            Console.Write("Header command [set/remove/clear]> ");
            string command = (Console.ReadLine() ?? String.Empty).Trim().ToLowerInvariant();
            switch (command)
            {
                case "set":
                    Console.Write("Header name> ");
                    string name = (Console.ReadLine() ?? String.Empty).Trim();
                    if (String.IsNullOrWhiteSpace(name))
                    {
                        WriteLine("Header name is required.");
                        return;
                    }

                    Console.Write("Header value> ");
                    string value = Console.ReadLine() ?? String.Empty;
                    _Headers[name] = value;
                    WriteLine("Header set.");
                    break;
                case "remove":
                    Console.Write("Header name> ");
                    string removeName = (Console.ReadLine() ?? String.Empty).Trim();
                    if (_Headers.Remove(removeName)) WriteLine("Header removed.");
                    else WriteLine("Header not found.");
                    break;
                case "clear":
                    _Headers.Clear();
                    WriteLine("Headers cleared.");
                    break;
                default:
                    WriteLine("No changes made.");
                    break;
            }
        }

        private static void ManageSubprotocols()
        {
            WriteLine("Subprotocols: " + (_Subprotocols.Count > 0 ? String.Join(", ", _Subprotocols) : "(none)"));
            Console.Write("Subprotocol command [add/remove/clear]> ");
            string command = (Console.ReadLine() ?? String.Empty).Trim().ToLowerInvariant();

            switch (command)
            {
                case "add":
                    Console.Write("Subprotocol> ");
                    string add = (Console.ReadLine() ?? String.Empty).Trim();
                    if (String.IsNullOrWhiteSpace(add))
                    {
                        WriteLine("Subprotocol is required.");
                        return;
                    }

                    bool exists = false;
                    for (int i = 0; i < _Subprotocols.Count; i++)
                    {
                        if (String.Equals(_Subprotocols[i], add, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                    {
                        _Subprotocols.Add(add);
                    }

                    WriteLine("Subprotocol added.");
                    break;
                case "remove":
                    Console.Write("Subprotocol> ");
                    string remove = (Console.ReadLine() ?? String.Empty).Trim();
                    _Subprotocols.RemoveAll(p => String.Equals(p, remove, StringComparison.OrdinalIgnoreCase));
                    WriteLine("Matching subprotocol entries removed.");
                    break;
                case "clear":
                    _Subprotocols.Clear();
                    WriteLine("Subprotocols cleared.");
                    break;
                default:
                    WriteLine("No changes made.");
                    break;
            }
        }

        private static void ManageTlsValidation()
        {
            WriteLine("Accept invalid certificates: " + (_AcceptInvalidCertificates ? "enabled" : "disabled"));
            Console.Write("Toggle? [y/N]> ");
            string choice = (Console.ReadLine() ?? String.Empty).Trim();

            if (choice.Equals("y", StringComparison.OrdinalIgnoreCase) || choice.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                _AcceptInvalidCertificates = !_AcceptInvalidCertificates;
                WriteLine("Accept invalid certificates: " + (_AcceptInvalidCertificates ? "enabled" : "disabled"));
            }
            else
            {
                WriteLine("No changes made.");
            }
        }

        private static void ManageClientGuid()
        {
            WriteLine("Client GUID: " + (_ClientGuid.HasValue ? _ClientGuid.Value.ToString() : "(none)"));
            Console.Write("GUID command [set/new/clear]> ");
            string command = (Console.ReadLine() ?? String.Empty).Trim().ToLowerInvariant();

            switch (command)
            {
                case "set":
                    Console.Write("GUID value> ");
                    string input = (Console.ReadLine() ?? String.Empty).Trim();
                    if (!Guid.TryParse(input, out Guid parsed))
                    {
                        WriteLine("Invalid GUID.");
                        return;
                    }

                    _ClientGuid = parsed;
                    WriteLine("Client GUID set.");
                    break;
                case "new":
                    _ClientGuid = Guid.NewGuid();
                    WriteLine("Client GUID set to " + _ClientGuid.Value);
                    break;
                case "clear":
                    _ClientGuid = null;
                    WriteLine("Client GUID cleared.");
                    break;
                default:
                    WriteLine("No changes made.");
                    break;
            }
        }

        private static WatsonWebSocketClient BuildClient()
        {
            WebSocketClientSettings settings = new WebSocketClientSettings();
            settings.AcceptInvalidCertificates = _AcceptInvalidCertificates;

            if (_ClientGuid.HasValue)
            {
                settings.ClientGuid = _ClientGuid.Value;
            }

            foreach (KeyValuePair<string, string> header in _Headers)
            {
                settings.Headers[header.Key] = header.Value;
            }

            for (int i = 0; i < _Subprotocols.Count; i++)
            {
                settings.RequestedSubprotocols.Add(_Subprotocols[i]);
            }

            return new WatsonWebSocketClient(new Uri(_Uri), settings);
        }

        private static void PrintConfigurationState()
        {
            WriteLine("URI: " + _Uri);
            WriteLine("Headers: " + (_Headers.Count > 0 ? String.Join(", ", BuildHeaderDisplay()) : "(none)"));
            WriteLine("Subprotocols: " + (_Subprotocols.Count > 0 ? String.Join(", ", _Subprotocols) : "(none)"));
            WriteLine("Accept invalid certificates: " + _AcceptInvalidCertificates);
            WriteLine("Client GUID: " + (_ClientGuid.HasValue ? _ClientGuid.Value.ToString() : "(none)"));
        }

        private static IEnumerable<string> BuildHeaderDisplay()
        {
            foreach (KeyValuePair<string, string> header in _Headers)
            {
                yield return header.Key + "=" + header.Value;
            }
        }

        private static void WriteLine(string message)
        {
            lock (_ConsoleSync)
            {
                Console.WriteLine(message);
            }
        }
    }
}
