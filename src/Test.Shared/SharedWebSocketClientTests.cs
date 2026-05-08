namespace Test.Shared
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Watson.Clients;
    using WatsonWebserver;
    using WatsonWebserver.Core;

    /// <summary>
    /// Shared websocket-client integration tests.
    /// </summary>
    public static class SharedWebSocketClientTests
    {
        /// <summary>
        /// Get the shared websocket-client tests.
        /// </summary>
        /// <returns>Ordered shared test cases.</returns>
        public static SharedNamedTestCase[] GetTests()
        {
            return new[]
            {
                new SharedNamedTestCase("WebSocketClient :: URI validation rejects non-websocket schemes", TestUriValidationRejectedAsync),
                new SharedNamedTestCase("WebSocketClient :: Host-and-port constructor builds expected URI", TestHostPortConstructorAsync),
                new SharedNamedTestCase("WebSocketClient :: Send before connect is rejected", TestSendBeforeConnectRejectedAsync),
                new SharedNamedTestCase("WebSocketClient :: Close before connect retains requested close state", TestCloseBeforeConnectRetainsStateAsync),
                new SharedNamedTestCase("WebSocketClient :: Connect over ws exposes raw socket and state", TestConnectExposesRawSocketAndStateAsync),
                new SharedNamedTestCase("WebSocketClient :: Repeated connect while connected is rejected", TestRepeatedConnectRejectedAsync),
                new SharedNamedTestCase("WebSocketClient :: TLS self-signed connection fails by default", TestTlsSelfSignedFailsByDefaultAsync),
                new SharedNamedTestCase("WebSocketClient :: TLS self-signed connection succeeds when explicitly allowed", TestTlsSelfSignedSucceedsWhenAllowedAsync),
                new SharedNamedTestCase("WebSocketClient :: Managed text send and receive work", TestManagedTextSendReceiveAsync),
                new SharedNamedTestCase("WebSocketClient :: Managed binary byte-array send works", TestManagedBinaryArraySendReceiveAsync),
                new SharedNamedTestCase("WebSocketClient :: Managed binary segment send works", TestManagedBinarySegmentSendReceiveAsync),
                new SharedNamedTestCase("WebSocketClient :: Large managed binary payloads round-trip intact", TestLargeBinaryRoundTripAsync),
                new SharedNamedTestCase("WebSocketClient :: Managed async enumeration receives sequential messages", TestManagedAsyncEnumerationAsync),
                new SharedNamedTestCase("WebSocketClient :: Concurrent managed receives are rejected", TestConcurrentManagedReceivesRejectedAsync),
                new SharedNamedTestCase("WebSocketClient :: Receive cancellation propagates and ends the active connection", TestReceiveCancellationEndsConnectionAsync),
                new SharedNamedTestCase("WebSocketClient :: Raw socket send interoperates with Watson-managed receive", TestRawSocketSendAsync),
                new SharedNamedTestCase("WebSocketClient :: Raw socket receive interoperates with Watson-managed send", TestRawSocketReceiveAsync),
                new SharedNamedTestCase("WebSocketClient :: Connection settings survive the upgrade request", TestConnectionSettingsSurviveUpgradeAsync),
                new SharedNamedTestCase("WebSocketClient :: Custom client GUID header names are honored", TestCustomClientGuidHeaderNameAsync),
                new SharedNamedTestCase("WebSocketClient :: ConfigureOptions callback is applied before connect", TestConfigureOptionsAppliedAsync),
                new SharedNamedTestCase("WebSocketClient :: Statistics advance correctly", TestStatisticsAdvanceAsync),
                new SharedNamedTestCase("WebSocketClient :: Server initiated close retains close state", TestServerInitiatedCloseRetainsStateAsync),
                new SharedNamedTestCase("WebSocketClient :: Server initiated close followed by reconnect works", TestServerInitiatedCloseThenReconnectAsync),
                new SharedNamedTestCase("WebSocketClient :: Client initiated close and reconnect work", TestClientInitiatedCloseAndReconnectAsync),
                new SharedNamedTestCase("WebSocketClient :: Dispose of an active client prevents further use", TestDisposeConnectedClientPreventsFurtherUseAsync),
                new SharedNamedTestCase("WebSocketClient :: Dispose is idempotent and clears raw socket", TestDisposeIdempotentAsync)
            };
        }

        private static Task TestUriValidationRejectedAsync()
        {
            AssertThrows<ArgumentException>(
                () => new WatsonWebSocketClient(new Uri("http://127.0.0.1:8181/ws")),
                "Expected non-websocket URI schemes to be rejected.");

            return Task.CompletedTask;
        }

        private static Task TestHostPortConstructorAsync()
        {
            using (WatsonWebSocketClient client = new WatsonWebSocketClient("127.0.0.1", 8181, false, "/ws/path"))
            {
                AssertEquals("ws://127.0.0.1:8181/ws/path", client.ServerUri.ToString(), "Expected the host-and-port constructor to build the expected websocket URI.");
                AssertTrue(client.RawSocket == null, "Expected the raw socket to be unavailable before connection.");
            }

            return Task.CompletedTask;
        }

        private static async Task TestSendBeforeConnectRejectedAsync()
        {
            using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:8181/ws/not-connected")))
            {
                await AssertThrowsAsync<IOException>(
                    async () => await client.SendTextAsync("not-connected").ConfigureAwait(false),
                    "Expected sends before connect to be rejected.");
            }
        }

        private static async Task TestCloseBeforeConnectRetainsStateAsync()
        {
            using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:8181/ws/not-connected")))
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "never-opened").ConfigureAwait(false);
                AssertEquals(WebSocketCloseStatus.NormalClosure, client.CloseStatus.Value, "Expected a pre-connect close to retain the requested close status.");
                AssertEquals("never-opened", client.CloseStatusDescription, "Expected a pre-connect close to retain the requested close description.");
                AssertEquals(WebSocketState.Closed, client.State, "Expected a pre-connect close to move the client into the closed state.");
                AssertTrue(!client.IsConnected, "Expected a pre-connect close to report a disconnected client.");
            }
        }

        private static async Task TestConnectExposesRawSocketAndStateAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/connect", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/connect")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    AssertTrue(client.IsConnected, "Expected the client to report as connected.");
                    AssertEquals(WebSocketState.Open, client.State, "Expected an open websocket state after connecting.");
                    AssertTrue(client.RawSocket != null, "Expected the raw socket to be available after connecting.");

                    await client.SendTextAsync("connected", timeout.Token).ConfigureAwait(false);
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(false);
                    AssertEquals(WebSocketState.Closed, client.State, "Expected the client to report a closed websocket state after graceful close.");
                }
            }
        }

        private static async Task TestRepeatedConnectRejectedAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/repeat-connect", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/repeat-connect")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await AssertThrowsAsync<InvalidOperationException>(
                        async () => await client.ConnectAsync(timeout.Token).ConfigureAwait(false),
                        "Expected repeated connect calls on an active client to be rejected.");

                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(false);
                }
            }
        }

        private static async Task TestTlsSelfSignedFailsByDefaultAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(true, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/secure", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("wss://127.0.0.1:" + host.Port.ToString() + "/ws/secure")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    bool threw = false;

                    try
                    {
                        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        threw = true;
                    }

                    AssertTrue(threw, "Expected self-signed TLS websocket connection to fail by default.");
                }
            }
        }

        private static async Task TestTlsSelfSignedSucceedsWhenAllowedAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(true, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/secure", async (ctx, session) =>
                {
                    await session.SendTextAsync("secure", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                WebSocketClientSettings settings = new WebSocketClientSettings();
                settings.AcceptInvalidCertificates = true;

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("wss://127.0.0.1:" + host.Port.ToString() + "/ws/secure"), settings))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    AssertTrue(client.IsConnected, "Expected TLS websocket connection to succeed when invalid certificates are explicitly allowed.");

                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals("secure", message.Text, "Expected TLS websocket server text payload to be received.");
                }
            }
        }

        private static async Task TestManagedTextSendReceiveAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/text", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("reply:" + inbound.Text, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/text")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendTextAsync("hello", timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals(WebSocketMessageType.Text, response.MessageType, "Expected a text response.");
                    AssertEquals("reply:hello", response.Text, "Expected managed text request-reply behavior.");
                }
            }
        }

        private static async Task TestManagedBinaryArraySendReceiveAsync()
        {
            byte[] payload = new byte[] { 1, 2, 3, 4, 5 };

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/binary-array", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendBinaryAsync(inbound.Data, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/binary-array")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendBinaryAsync(payload, timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals(WebSocketMessageType.Binary, response.MessageType, "Expected a binary response.");
                    AssertBytesEqual(payload, response.Data, "Expected binary byte-array echo behavior.");
                }
            }
        }

        private static async Task TestManagedBinarySegmentSendReceiveAsync()
        {
            byte[] payload = new byte[] { 10, 20, 30, 40, 50, 60 };
            ArraySegment<byte> segment = new ArraySegment<byte>(payload, 1, 4);
            byte[] expected = new byte[] { 20, 30, 40, 50 };

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/binary-segment", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendBinaryAsync(inbound.Data, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/binary-segment")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendBinaryAsync(segment, timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertBytesEqual(expected, response.Data, "Expected binary segment send behavior to preserve the provided slice.");
                }
            }
        }

        private static async Task TestLargeBinaryRoundTripAsync()
        {
            byte[] payload = new byte[131072];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 251);
            }

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/large-binary", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendBinaryAsync(inbound.Data, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/large-binary")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendBinaryAsync(payload, timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals(WebSocketMessageType.Binary, response.MessageType, "Expected a binary response for a large binary round-trip.");
                    AssertEquals(payload.Length, response.Length, "Expected large binary round-trip payload length to be preserved.");
                    AssertBytesEqual(payload, response.Data, "Expected large binary round-trip payload content to be preserved.");
                }
            }
        }

        private static async Task TestManagedAsyncEnumerationAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/enumerate", async (ctx, session) =>
                {
                    await session.SendTextAsync("one", ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("two", ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("three", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/enumerate")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    List<string> received = new List<string>();
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

                    await foreach (WebSocketMessage message in client.ReadMessagesAsync(timeout.Token).ConfigureAwait(false))
                    {
                        received.Add(message.Text);
                    }

                    AssertEquals(3, received.Count, "Expected three text messages from managed async enumeration.");
                    AssertEquals("one", received[0], "Expected the first enumerated websocket message to match.");
                    AssertEquals("two", received[1], "Expected the second enumerated websocket message to match.");
                    AssertEquals("three", received[2], "Expected the third enumerated websocket message to match.");
                }
            }
        }

        private static async Task TestConcurrentManagedReceivesRejectedAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/recv", async (ctx, session) =>
                {
                    await Task.Delay(250, ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("ready", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/recv")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

                    Task<WebSocketMessage> first = client.ReceiveAsync(timeout.Token);
                    await AssertThrowsAsync<InvalidOperationException>(
                        async () => await client.ReceiveAsync(timeout.Token).ConfigureAwait(false),
                        "Expected concurrent managed receives to be rejected.");

                    WebSocketMessage response = await first.ConfigureAwait(false);
                    AssertEquals("ready", response.Text, "Expected the first receive to complete successfully.");
                }
            }
        }

        private static async Task TestReceiveCancellationEndsConnectionAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/cancel-recv", async (ctx, session) =>
                {
                    await Task.Delay(250, ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("after-cancel", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/cancel-recv")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (CancellationTokenSource receiveCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

                    await AssertThrowsAsync<OperationCanceledException>(
                        async () => await client.ReceiveAsync(receiveCancel.Token).ConfigureAwait(false),
                        "Expected receive cancellation to bubble an OperationCanceledException.");

                    AssertTrue(!client.IsConnected, "Expected receive cancellation to end the active websocket connection on current runtimes.");
                }
            }
        }

        private static async Task TestRawSocketSendAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/raw-send", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage message = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("ack:" + message.Text, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/raw-send")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

                    byte[] bytes = Encoding.UTF8.GetBytes("raw-send");
                    await client.RawSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals("ack:raw-send", response.Text, "Expected raw socket send to interoperate with Watson-managed receive.");
                }
            }
        }

        private static async Task TestRawSocketReceiveAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/raw-recv", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("raw-response", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/raw-recv")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendTextAsync("go", timeout.Token).ConfigureAwait(false);

                    string text = await ReceiveTextRawAsync(client.RawSocket, timeout.Token).ConfigureAwait(false);
                    AssertEquals("raw-response", text, "Expected raw socket receive to interoperate with Watson-managed send.");
                }
            }
        }

        private static async Task TestConnectionSettingsSurviveUpgradeAsync()
        {
            TaskCompletionSource<string> headerSource = CreateTaskCompletionSource<string>();
            TaskCompletionSource<string> cookieSource = CreateTaskCompletionSource<string>();
            TaskCompletionSource<string[]> subprotocolSource = CreateTaskCompletionSource<string[]>();

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.Settings.WebSockets.AllowClientSuppliedGuid = true;
                server.WebSocket("/ws/settings", async (ctx, session) =>
                {
                    headerSource.TrySetResult(session.Request.Headers["x-test-header"]);
                    cookieSource.TrySetResult(session.Request.Headers["Cookie"]);
                    subprotocolSource.TrySetResult(new List<string>(session.Request.RequestedSubprotocols).ToArray());
                    await session.SendTextAsync(session.Id.ToString(), ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                Guid desiredGuid = Guid.NewGuid();
                WebSocketClientSettings settings = new WebSocketClientSettings();
                settings.Headers["x-test-header"] = "header-value";
                settings.Cookies.Add(new System.Net.Cookie("sample", "cookie", "/", "127.0.0.1"));
                settings.RequestedSubprotocols.Add("chat");
                settings.RequestedSubprotocols.Add("superchat");
                settings.ClientGuid = desiredGuid;

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/settings"), settings))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                    AssertEquals("header-value", await headerSource.Task.ConfigureAwait(false), "Expected custom request header to survive the upgrade request.");
                    AssertTrue((await cookieSource.Task.ConfigureAwait(false)).Contains("sample=cookie"), "Expected cookie header to survive the upgrade request.");
                    string[] requestedSubprotocols = await subprotocolSource.Task.ConfigureAwait(false);
                    AssertEquals(2, requestedSubprotocols.Length, "Expected requested subprotocols to be captured on the server.");
                    AssertEquals("chat", requestedSubprotocols[0], "Expected the first requested subprotocol to match.");
                    AssertEquals("superchat", requestedSubprotocols[1], "Expected the second requested subprotocol to match.");
                    AssertEquals(desiredGuid.ToString(), message.Text, "Expected the server to honor the client-supplied GUID header.");
                }
            }
        }

        private static async Task TestCustomClientGuidHeaderNameAsync()
        {
            TaskCompletionSource<string> headerSource = CreateTaskCompletionSource<string>();

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.Settings.WebSockets.AllowClientSuppliedGuid = true;
                server.Settings.WebSockets.ClientGuidHeaderName = "x-client-guid";
                server.WebSocket("/ws/custom-guid", async (ctx, session) =>
                {
                    headerSource.TrySetResult(session.Request.Headers["x-client-guid"]);
                    await session.SendTextAsync(session.Id.ToString(), ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                Guid desiredGuid = Guid.NewGuid();
                WebSocketClientSettings settings = new WebSocketClientSettings();
                settings.ClientGuid = desiredGuid;
                settings.ClientGuidHeaderName = "x-client-guid";

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/custom-guid"), settings))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                    AssertEquals(desiredGuid.ToString(), await headerSource.Task.ConfigureAwait(false), "Expected the custom client GUID header name to be used during connect.");
                    AssertEquals(desiredGuid.ToString(), message.Text, "Expected the server to honor the custom client GUID header.");
                }
            }
        }

        private static async Task TestConfigureOptionsAppliedAsync()
        {
            TaskCompletionSource<string> callbackHeaderSource = CreateTaskCompletionSource<string>();

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/options", async (ctx, session) =>
                {
                    callbackHeaderSource.TrySetResult(session.Request.Headers["x-configured"]);
                    await session.SendTextAsync("configured", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                WebSocketClientSettings settings = new WebSocketClientSettings();
                bool callbackInvoked = false;
                settings.ConfigureOptions = options =>
                {
                    callbackInvoked = true;
                    options.SetRequestHeader("x-configured", "true");
                };

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/options"), settings))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);

                    AssertTrue(callbackInvoked, "Expected the ClientWebSocketOptions configuration callback to be invoked before connecting.");
                    AssertEquals("configured", message.Text, "Expected the server to acknowledge the configured client.");
                    AssertEquals("true", await callbackHeaderSource.Task.ConfigureAwait(false), "Expected the options callback to be able to set request headers.");
                }
            }
        }

        private static async Task TestStatisticsAdvanceAsync()
        {
            TaskCompletionSource<WebSocketClientStatistics> statsSource = CreateTaskCompletionSource<WebSocketClientStatistics>();

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/stats", async (ctx, session) =>
                {
                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("ack:" + inbound.Text, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/stats")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendTextAsync("stats", timeout.Token).ConfigureAwait(false);
                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals("ack:stats", message.Text, "Expected statistics test request-reply behavior.");

                    WebSocketClientStatistics snapshot = client.Statistics.Snapshot();
                    statsSource.TrySetResult(snapshot);

                    WebSocketClientStatistics stats = await statsSource.Task.ConfigureAwait(false);
                    AssertEquals(1L, stats.MessagesSent, "Expected one sent websocket message to be counted.");
                    AssertEquals(1L, stats.MessagesReceived, "Expected one received websocket message to be counted.");
                    AssertTrue(stats.BytesSent > 0, "Expected sent websocket bytes to be counted.");
                    AssertTrue(stats.BytesReceived > 0, "Expected received websocket bytes to be counted.");
                }
            }
        }

        private static async Task TestServerInitiatedCloseRetainsStateAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/server-close", async (ctx, session) =>
                {
                    await session.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server-close", ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/server-close")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    WebSocketMessage message = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertTrue(message == null, "Expected managed receive to return null after a server-initiated close.");
                    AssertEquals(WebSocketCloseStatus.EndpointUnavailable, client.CloseStatus.Value, "Expected the close status to be retained after server close.");
                    AssertEquals("server-close", client.CloseStatusDescription, "Expected the close description to be retained after server close.");
                    AssertEquals(WebSocketState.Closed, client.State, "Expected the client to report a closed state after server close.");
                }
            }
        }

        private static async Task TestServerInitiatedCloseThenReconnectAsync()
        {
            int connections = 0;

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/server-close-reconnect", async (ctx, session) =>
                {
                    if (Interlocked.Increment(ref connections) == 1)
                    {
                        await session.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server-reconnect-close", ctx.Token).ConfigureAwait(false);
                        return;
                    }

                    WatsonWebserver.Core.WebSockets.WebSocketMessage inbound = await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    await session.SendTextAsync("ack:" + inbound.Text, ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/server-close-reconnect")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    WebSocketMessage closedMessage = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertTrue(closedMessage == null, "Expected the first connection to close immediately.");

                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.SendTextAsync("reconnected", timeout.Token).ConfigureAwait(false);

                    WebSocketMessage response = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    AssertEquals("ack:reconnected", response.Text, "Expected reconnect after server-initiated close to succeed.");
                }
            }
        }

        private static async Task TestClientInitiatedCloseAndReconnectAsync()
        {
            TaskCompletionSource<bool> serverObservedClose = CreateTaskCompletionSource<bool>();

            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/reconnect", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                    serverObservedClose.TrySetResult(true);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                using (WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/reconnect")))
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "client-close", timeout.Token).ConfigureAwait(false);
                    AssertEquals(WebSocketCloseStatus.NormalClosure, client.CloseStatus.Value, "Expected client-initiated close status to be retained.");
                    AssertEquals(WebSocketState.Closed, client.State, "Expected the client to report a closed state after client close.");

                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    AssertTrue(client.IsConnected, "Expected reconnect after graceful close to succeed.");

                    await client.SendTextAsync("done", timeout.Token).ConfigureAwait(false);
                    await serverObservedClose.Task.ConfigureAwait(false);
                }
            }
        }

        private static async Task TestDisposeConnectedClientPreventsFurtherUseAsync()
        {
            using (LoopbackServerHost host = new LoopbackServerHost(false, false, false, server =>
            {
                server.Settings.WebSockets.Enable = true;
                server.WebSocket("/ws/dispose-live", async (ctx, session) =>
                {
                    await session.ReceiveAsync(ctx.Token).ConfigureAwait(false);
                });
            }))
            {
                await host.StartAsync().ConfigureAwait(false);

                WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:" + host.Port.ToString() + "/ws/dispose-live"));
                using (client)
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                    client.Dispose();

                    AssertTrue(client.RawSocket == null, "Expected dispose of a connected client to clear the raw socket.");
                    AssertEquals(WebSocketState.Closed, client.State, "Expected dispose of a connected client to retain a closed state.");
                    await AssertThrowsAsync<ObjectDisposedException>(
                        async () => await client.ConnectAsync(timeout.Token).ConfigureAwait(false),
                        "Expected dispose of a connected client to prevent further use.");
                }
            }
        }

        private static Task TestDisposeIdempotentAsync()
        {
            WatsonWebSocketClient client = new WatsonWebSocketClient(new Uri("ws://127.0.0.1:8181/ws/dispose"));
            client.Dispose();
            client.Dispose();
            AssertTrue(client.RawSocket == null, "Expected the raw socket to be null after dispose.");
            AssertEquals(WebSocketState.Closed, client.State, "Expected the client to report a closed state after dispose.");
            return Task.CompletedTask;
        }

        private static async Task<string> ReceiveTextRawAsync(ClientWebSocket socket, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                int offset = 0;

                while (true)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return null;

                    offset += result.Count;
                    if (result.EndOfMessage)
                    {
                        return Encoding.UTF8.GetString(buffer, 0, offset);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static TaskCompletionSource<T> CreateTaskCompletionSource<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual, string message)
        {
            if (expected == null && actual == null) return;
            if (expected == null || actual == null) throw new InvalidOperationException(message);
            if (expected.Length != actual.Length) throw new InvalidOperationException(message + " Length mismatch.");

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new InvalidOperationException(message + " Byte mismatch at index " + i + ".");
                }
            }
        }

        private static void AssertEquals<T>(T expected, T actual, string message)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected: " + expected + " Actual: " + actual);
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
    }
}
