# Watson.Clients

`Watson.Clients` provides `WatsonWebSocketClient`, an async-first WebSocket client for Watson 7.

Key features:
- explicit `ConnectAsync`, `ReceiveAsync`, and `ReadMessagesAsync`
- whole-message text and binary receive semantics
- serialized Watson-managed send path
- direct raw `ClientWebSocket` access through `RawSocket`
- headers, cookies, requested subprotocols, GUID-header support, and TLS customization

Example:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Watson.Clients;

WebSocketClientSettings settings = new WebSocketClientSettings();
WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"),
    settings);

await client.ConnectAsync();
await client.SendTextAsync("hello");
WebSocketMessage message = await client.ReceiveAsync(CancellationToken.None);
Console.WriteLine(message.Text);
await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done");
client.Dispose();
```

Use `RawSocket` only for advanced scenarios where direct `ClientWebSocket` access is necessary. Mixing raw receives with Watson-managed `ReceiveAsync()` or `ReadMessagesAsync()` on the same connection is unsupported.
