# Watson.Clients

`Watson.Clients` provides `WatsonWebSocketClient`, an async-first WebSocket client for Watson 7.

It is intended for developers who want explicit control over connection lifecycle, message flow, and `ClientWebSocket` behavior without a hidden background callback model.

Core capabilities:

- Explicit `ConnectAsync`, `ReceiveAsync`, `ReadMessagesAsync`, and `CloseAsync` lifecycle
- Whole-message text and binary receive semantics
- Serialized Watson-managed send path
- Headers, cookies, requested subprotocols, keepalive, GUID-header interop, and TLS customization
- Reconnect support after graceful close or server-initiated close
- Connection statistics
- Advanced raw `ClientWebSocket` access through `RawSocket`

If your process also hosts a Watson server, reference both packages:

- `Watson` for inbound HTTP/WebSocket server behavior
- `Watson.Clients` for outbound WebSocket client behavior

Related docs:

- Server README: https://github.com/dotnet/WatsonWebserver/blob/main/README.md
- WebSockets API: https://github.com/dotnet/WatsonWebserver/blob/main/WEBSOCKETS_API.md
- Migration guide: https://github.com/dotnet/WatsonWebserver/blob/main/MIGRATING_FROM_WATSONWEBSOCKET.md

## Install

```powershell
dotnet add package Watson.Clients
```

## Supported Frameworks

`Watson.Clients` currently targets:

- `netstandard2.1`
- `net462`
- `net48`
- `net8.0`
- `net10.0`

## Quick Start

```csharp
using System;
using System.Net.WebSockets;
using Watson.Clients;

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"));

await client.ConnectAsync();
await client.SendTextAsync("hello");

WebSocketMessage reply = await client.ReceiveAsync();
if (reply != null)
{
    Console.WriteLine(reply.Text);
}

await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done");
```

## Client Model

`WatsonWebSocketClient` is explicit and async-first:

1. Create a client from a `ws://` or `wss://` URI, or from host/port/path parts.
2. Call `ConnectAsync()`.
3. Send with `SendTextAsync()` or `SendBinaryAsync()`.
4. Receive with either `ReceiveAsync()` or `ReadMessagesAsync()`.
5. Close with `CloseAsync(...)`.
6. Dispose the client when you are done with it.

Important behavioral rules:

- Only `ws` and `wss` URIs are accepted.
- The client does not start a hidden message pump for you.
- Only one Watson-managed receive operation may be active at a time per client.
- `ReceiveAsync()` returns `null` when the connection closes.
- `Dispose()` is terminal; a disposed client cannot be reused.

## Public Types

`Watson.Clients` currently exposes four public types:

| Type | Purpose |
|---|---|
| `WatsonWebSocketClient` | Main client lifecycle, send, receive, close, reconnect, and raw socket access |
| `WebSocketClientSettings` | Upgrade request, TLS, GUID-header, keepalive, and close-handshake configuration |
| `WebSocketMessage` | Whole-message text or binary payload container |
| `WebSocketClientStatistics` | Message and payload-byte counters with snapshot support |

## Creating A Client

You can construct the client from a `Uri`:

```csharp
WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"));
```

Or from host, port, TLS flag, and path:

```csharp
WatsonWebSocketClient client = new WatsonWebSocketClient(
    "127.0.0.1",
    8181,
    ssl: false,
    path: "/ws/echo");
```

The host/port constructor normalizes the path for you. If the path does not start with `/`, Watson adds it.

## Connection Lifecycle

Useful lifecycle and state members:

- `ServerUri`: destination endpoint
- `IsConnected`: `true` when the tracked socket state is `Open`, `CloseReceived`, or `CloseSent`
- `State`: current tracked `WebSocketState`
- `CloseStatus`: final or observed `WebSocketCloseStatus`
- `CloseStatusDescription`: final or observed close description
- `Subprotocol`: negotiated subprotocol, if one was selected by the server

### Connecting

```csharp
await client.ConnectAsync();
```

Behavior notes:

- Calling `ConnectAsync()` while already connected throws `InvalidOperationException`.
- Self-signed or otherwise untrusted TLS certificates fail by default.
- After a graceful close or server-initiated close, you can call `ConnectAsync()` again on the same client instance.

### Closing

```csharp
await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "client shutdown");
```

Behavior notes:

- `CloseAsync(...)` performs a graceful close handshake when the socket is connected.
- `CloseAsync(...)` stores the requested close status and reason even if the socket was never connected.
- `CloseHandshakeTimeoutMs` controls how long the client waits for graceful close before aborting.
- Values below `1000` ms are effectively clamped to `1000` ms during close.

### Dispose

```csharp
client.Dispose();
```

Disposal:

- Cancels the internal lifetime token
- Disposes the underlying `ClientWebSocket`
- Clears `RawSocket`
- Moves tracked state to `Closed`
- Prevents any future use of the client instance

## Sending And Receiving

### Whole-Message Semantics

Watson delivers complete WebSocket messages, not frames. If the server sends a fragmented message, Watson reassembles it before returning a `WebSocketMessage`.

### Text Send And Receive

```csharp
using System;
using Watson.Clients;

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"));

await client.ConnectAsync();
await client.SendTextAsync("hello");

WebSocketMessage message = await client.ReceiveAsync();
if (message != null && message.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
{
    Console.WriteLine(message.Text);
}
```

### Binary Send And Receive

```csharp
byte[] payload = new byte[] { 1, 2, 3, 4, 5 };

await client.ConnectAsync();
await client.SendBinaryAsync(payload);

WebSocketMessage reply = await client.ReceiveAsync();
if (reply != null)
{
    System.Console.WriteLine($"Received {reply.Length} bytes");
}
```

You can also send a slice using `ArraySegment<byte>`:

```csharp
byte[] buffer = new byte[] { 10, 20, 30, 40, 50, 60 };
ArraySegment<byte> segment = new ArraySegment<byte>(buffer, 1, 4);

await client.SendBinaryAsync(segment);
```

### Receive One Message

Use `ReceiveAsync()` when you want exactly one message at a time:

```csharp
WebSocketMessage message = await client.ReceiveAsync();
if (message == null)
{
    System.Console.WriteLine("Connection closed");
}
else if (message.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
{
    System.Console.WriteLine(message.Text);
}
else
{
    System.Console.WriteLine(System.BitConverter.ToString(message.Data));
}
```

### Read Messages Continuously

Use `ReadMessagesAsync()` when the connection should be consumed as a stream of messages:

```csharp
using System;
using System.Net.WebSockets;
using System.Threading;
using Watson.Clients;

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/broadcast/general"));

using CancellationTokenSource cts = new CancellationTokenSource();

await client.ConnectAsync(cts.Token);

await foreach (WebSocketMessage message in client.ReadMessagesAsync(cts.Token))
{
    if (message.MessageType == WebSocketMessageType.Text)
    {
        Console.WriteLine(message.Text);
    }
}

Console.WriteLine(
    $"Closed: {client.CloseStatus} {client.CloseStatusDescription}");
```

### Managed Receive Concurrency Rule

Only one Watson-managed receive operation may be active at a time:

- `ReceiveAsync()` and `ReceiveAsync()` concurrently: not allowed
- `ReceiveAsync()` and `ReadMessagesAsync()` concurrently: not allowed
- `ReadMessagesAsync()` and `ReadMessagesAsync()` concurrently: not allowed

If you violate this rule, Watson throws `InvalidOperationException`.

### Receive Cancellation

Receive cancellation is surfaced to the caller as `OperationCanceledException`.

On current runtimes, canceling an active Watson-managed receive may also end the live connection. If you need a long-running receive loop, use a deliberate connection-scoped `CancellationToken` and treat cancellation as a shutdown signal rather than a harmless pause.

## WebSocketClientSettings

`WebSocketClientSettings` controls the upgrade request and close behavior.

```csharp
using System;
using System.Net;
using Watson.Clients;

WebSocketClientSettings settings = new WebSocketClientSettings
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    AcceptInvalidCertificates = true,
    ClientGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
    ClientGuidHeaderName = "x-client-guid",
    CloseHandshakeTimeoutMs = 3000,
    ConfigureOptions = options =>
    {
        options.SetRequestHeader("x-configured", "true");
    }
};

settings.Headers["x-tenant-id"] = "tenant-42";
settings.RequestedSubprotocols.Add("chat");
settings.RequestedSubprotocols.Add("superchat");
settings.Cookies.Add(new Cookie("session", "abc123", "/", "127.0.0.1"));
```

### Settings Reference

| Setting | Default | Purpose |
|---|---|---|
| `Headers` | empty dictionary | Request headers added before connect |
| `RequestedSubprotocols` | empty list | WebSocket subprotocols requested during handshake |
| `Cookies` | new `CookieContainer()` | Cookies sent during upgrade |
| `KeepAliveInterval` | `00:00:30` | Applied to the underlying `ClientWebSocketOptions` |
| `AcceptInvalidCertificates` | `false` | Accept invalid or untrusted TLS certificates |
| `ClientGuid` | `null` | Optional GUID sent in a request header |
| `ClientGuidHeaderName` | `"x-guid"` | Header name used when sending `ClientGuid` |
| `CloseHandshakeTimeoutMs` | `5000` | Graceful close handshake timeout |
| `ConfigureOptions` | `null` | Last-mile callback for customizing `ClientWebSocketOptions` |

Notes:

- `AcceptInvalidCertificates = true` should be limited to development, test, or controlled internal environments.
- `ConfigureOptions` runs after Watson applies headers, cookies, subprotocols, GUID-header behavior, keepalive, and TLS validation settings.
- `ClientGuid` is only sent when it has a non-empty value.

## WebSocketMessage

`WebSocketMessage` represents one whole inbound or outbound message.

Useful members:

- `MessageType`: `Text` or `Binary`
- `Data`: raw payload bytes
- `Text`: UTF-8 decoded payload for text messages, otherwise `null`
- `Length`: payload length in bytes

Convenience factories:

- `WebSocketMessage.FromText(string data)`
- `WebSocketMessage.FromBinary(byte[] data)`

## Statistics

`client.Statistics` exposes message and payload-byte counters:

- `MessagesSent`
- `MessagesReceived`
- `BytesSent`
- `BytesReceived`

Use `Snapshot()` when you want a stable copy of the current counters:

```csharp
WebSocketClientStatistics stats = client.Statistics.Snapshot();

System.Console.WriteLine($"Messages sent: {stats.MessagesSent}");
System.Console.WriteLine($"Messages received: {stats.MessagesReceived}");
System.Console.WriteLine($"Bytes sent: {stats.BytesSent}");
System.Console.WriteLine($"Bytes received: {stats.BytesReceived}");
```

These counters track successful Watson-managed sends and successful Watson-managed receives at the payload level.

## RawSocket Escape Hatch

`RawSocket` exposes the underlying `System.Net.WebSockets.ClientWebSocket`.

This is intentionally an advanced escape hatch. Use it only when you need framework-level features that are not exposed directly by Watson.

Supported patterns:

- Watson-managed send with Watson-managed receive
- Raw socket send with Watson-managed receive
- Watson-managed send with raw socket receive

Unsupported pattern:

- Mixing raw receives with Watson-managed `ReceiveAsync()` or `ReadMessagesAsync()` on the same live connection

Also note:

- Watson serializes Watson-managed sends with an internal lock.
- If you send through `RawSocket`, you are responsible for your own coordination.
- `RawSocket` is `null` before `ConnectAsync()` and after `Dispose()`.

Example:

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using Watson.Clients;

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"));

await client.ConnectAsync();

byte[] bytes = Encoding.UTF8.GetBytes("hello from raw socket");
await client.RawSocket.SendAsync(
    new ArraySegment<byte>(bytes),
    WebSocketMessageType.Text,
    true,
    default);

WebSocketMessage reply = await client.ReceiveAsync();
Console.WriteLine(reply?.Text);
```

## Watson Server Interop

`Watson.Clients` is designed to interoperate cleanly with Watson 7 server-side WebSocket routes.

Typical Watson 7 server example:

```csharp
using System.Threading.Tasks;
using WatsonWebserver;
using WatsonWebserver.Core;

WebserverSettings settings = new WebserverSettings("127.0.0.1", 8181);
settings.WebSockets.Enable = true;
settings.WebSockets.AllowClientSuppliedGuid = true;
settings.WebSockets.ClientGuidHeaderName = "x-client-guid";

Webserver server = new Webserver(settings, DefaultRoute);

server.WebSocket("/ws/echo", async (ctx, session) =>
{
    await foreach (WatsonWebserver.Core.WebSockets.WebSocketMessage message in session.ReadMessagesAsync(ctx.Token))
    {
        if (message.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
        {
            await session.SendTextAsync("echo:" + message.Text, ctx.Token);
        }
    }
});

static async Task DefaultRoute(HttpContextBase ctx)
{
    ctx.Response.StatusCode = 404;
    await ctx.Response.Send("Not found");
}
```

Matching client example:

```csharp
using System;
using Watson.Clients;

WebSocketClientSettings settings = new WebSocketClientSettings();
settings.ClientGuid = Guid.NewGuid();
settings.ClientGuidHeaderName = "x-client-guid";
settings.Headers["x-demo"] = "watson";

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"),
    settings);
```

Interop notes:

- If you want the server to honor a client-supplied GUID, the server must enable `AllowClientSuppliedGuid`.
- If you change the GUID header name on one side, change it on the other side too.
- Requested subprotocols are sent in the handshake; `Subprotocol` tells you what the server selected.
- Current Watson 7 server-side WebSocket support is HTTP/1.1 only.

## Reconnect Pattern

Reconnect is supported after close:

```csharp
using System;
using System.Net.WebSockets;
using Watson.Clients;

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/reconnect"));

await client.ConnectAsync();
await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "first session complete");

await client.ConnectAsync();
await client.SendTextAsync("reconnected");

WebSocketMessage reply = await client.ReceiveAsync();
Console.WriteLine(reply?.Text);
```

What is not supported is reconnect after `Dispose()`. Disposal permanently retires the client instance.

## Migration From WatsonWebsocket 4.x

The most important client-side change is the programming model:

- Old WatsonWebsocket client usage was callback/event oriented.
- `Watson.Clients` is explicit and receive-loop oriented.

Common mappings:

| WatsonWebsocket 4.x | Watson.Clients |
|---|---|
| `WatsonWsClient` | `WatsonWebSocketClient` |
| `StartAsync()` | `ConnectAsync()` |
| `StopAsync()` | `CloseAsync(...)` and `Dispose()` |
| `Connected` | `IsConnected` |
| `ServerConnected` event | successful `ConnectAsync()` plus `State` / `IsConnected` |
| `ServerDisconnected` event | `CloseStatus`, `CloseStatusDescription`, and `State` after close |
| `MessageReceived` event | `ReceiveAsync()` or `ReadMessagesAsync()` |
| `SendAsync(string)` | `SendTextAsync(string)` |
| `SendAsync(byte[])` | `SendBinaryAsync(byte[])` |
| `SendAndWaitAsync()` | explicit send followed by explicit receive |

For the full migration guide, see:

https://github.com/dotnet/WatsonWebserver/blob/main/MIGRATING_FROM_WATSONWEBSOCKET.md

## Limitations And Behavior Notes

- There is no hidden background event pump.
- There is no built-in `SendAndWaitAsync()` helper in v1.
- Only one Watson-managed receive operation may be active at a time.
- `ReceiveAsync()` returns `null` when the peer closes the connection.
- Invalid or self-signed TLS certificates are rejected by default.
- Canceling an active Watson-managed receive may end the live connection on current runtimes.
- `RawSocket` is for advanced scenarios; do not mix raw receives with Watson-managed receives.
- A disposed client cannot be used again.

## Samples And Tests

If you want working reference code from this repository:

- Interactive sample client: https://github.com/dotnet/WatsonWebserver/tree/main/src/Test.WebsocketClient
- Shared client integration tests: https://github.com/dotnet/WatsonWebserver/blob/main/src/Test.Shared/SharedWebSocketClientTests.cs
- Server-side WebSocket guide: https://github.com/dotnet/WatsonWebserver/blob/main/WEBSOCKETS_API.md

The tests are especially useful because they document the expected behavior for reconnect, TLS certificate handling, GUID-header interop, raw socket usage, close-state retention, statistics, and receive-loop rules.
