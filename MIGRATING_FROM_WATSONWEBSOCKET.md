# Migrating From WatsonWebsocket 4.x

This guide maps WatsonWebsocket 4.x to the Watson 7 WebSocket surface.

Server functionality now lives in `Watson`. Client functionality now lives in `Watson.Clients`.

## Scope

This migration guide covers both:

- WatsonWebsocket server-side usage patterns
- WatsonWebsocket client usage patterns built around `WatsonWsClient`

Watson 7 WebSocket support is currently:

- HTTP/1.1 only
- route-oriented on the server
- session-oriented on the server
- message-oriented on both server and client

## Package Split

Install the package that matches the role your application is playing:

```powershell
dotnet add package Watson
```

```powershell
dotnet add package Watson.Clients
```

Use `Watson` for inbound server handling. Use `Watson.Clients` for outbound websocket client connections. Applications that act as both server and client can reference both packages.

See also:

- [README.md](README.md) for repo-level package guidance
- [src/Watson.Clients/README.md](src/Watson.Clients/README.md) for the focused client guide

## Concept Mapping

### Server concepts

| WatsonWebsocket 4.x | Watson 7 |
|---|---|
| server-level websocket library | built into `Watson` |
| `ClientConnected` event | route start plus `server.Events.WebSocketSessionStarted` |
| `ClientDisconnected` event | `server.Events.WebSocketSessionEnded` |
| `MessageReceived` event | `session.ReceiveAsync()` / `session.ReadMessagesAsync()` |
| client GUID header | `Settings.WebSockets.ClientGuidHeaderName` |
| `ListClients()` | `ListWebSocketSessions()` |
| `IsClientConnected(Guid)` | `IsWebSocketSessionConnected(Guid)` |
| `DisconnectClient(Guid)` | `DisconnectWebSocketSessionAsync(...)` |

### Client concepts

| WatsonWebsocket 4.x | Watson 7 |
|---|---|
| `WatsonWsClient` | `WatsonWebSocketClient` in `Watson.Clients` |
| `ServerConnected` event | successful `ConnectAsync()` plus `IsConnected` / `State` |
| `ServerDisconnected` event | `CloseStatus`, `CloseStatusDescription`, and `State` after close or receive-loop termination |
| `MessageReceived` event | `ReceiveAsync()` / `ReadMessagesAsync()` |
| string send helpers | `SendTextAsync()` |
| binary send helpers | `SendBinaryAsync()` |
| connection headers, cookies, subprotocols, GUID header, TLS knobs | `WebSocketClientSettings` |
| direct low-level client socket use | `WatsonWebSocketClient.RawSocket` |
| disconnect helper | `CloseAsync(...)` followed by `Dispose()` |

There is no v1 Watson 7 equivalent of `SendAndWaitAsync()`. The intended pattern is explicit `Send...Async()` followed by explicit `ReceiveAsync()`.

### Client method and property mapping

| WatsonWebsocket 4.x | Watson 7 |
|---|---|
| `StartAsync()` | `ConnectAsync()` |
| `StopAsync()` | `CloseAsync(...)` and `Dispose()` |
| `Connected` | `IsConnected` |
| `ServerIpPort`-style endpoint information | `ServerUri` |
| message callbacks | `ReceiveAsync()` / `ReadMessagesAsync()` |
| `SendAsync(string)` | `SendTextAsync(string)` |
| `SendAsync(byte[])` | `SendBinaryAsync(byte[])` |
| `SendAndWaitAsync()` | intentionally not shipped in v1 |
| connection headers/cookies/subprotocols/GUID/TLS options | `WebSocketClientSettings` |
| direct framework socket access | `RawSocket` |

## Major Behavioral Changes

### 1. Server routing is first-class

WatsonWebsocket centered the application model around websocket lifecycle events.

Watson 7 centers the model around route handlers:

```csharp
server.WebSocket("/chat", async (ctx, session) =>
{
    await foreach (WebSocketMessage message in session.ReadMessagesAsync(ctx.Token))
    {
        await session.SendTextAsync("echo:" + message.Text, ctx.Token);
    }
});
```

### 2. Per-message global callbacks are not the primary API

In Watson 7, application message handling belongs inside the websocket route handler or inside your explicit client receive loop.

Server-level websocket events are observability-only:

- `WebSocketSessionStarted`
- `WebSocketSessionEnded`
- `WebSocketHandshakeFailed`

### 3. Same-path HTTP and WebSocket routing is supported

You can now use the same path for both HTTP and WebSocket behavior:

```csharp
server.Get("/chat", async req => new { Mode = "http" });

server.WebSocket("/chat", async (ctx, session) =>
{
    await session.SendTextAsync("connected", ctx.Token);
});
```

### 4. Client-supplied GUIDs are opt-in on the server

Watson 7 defaults:

- `Settings.WebSockets.AllowClientSuppliedGuid = false`
- `Settings.WebSockets.ClientGuidHeaderName = "x-guid"`

To restore compatibility with client-supplied GUID behavior:

```csharp
server.Settings.WebSockets.Enable = true;
server.Settings.WebSockets.AllowClientSuppliedGuid = true;
server.Settings.WebSockets.ClientGuidHeaderName = "x-guid";
```

On the client side, set `WebSocketClientSettings.ClientGuid` and optionally `ClientGuidHeaderName`.

### 5. Receive is whole-message and explicitly owned

Watson 7 preserves whole-message delivery, but receive operations are explicitly owned by `WebSocketSession` on the server and `WatsonWebSocketClient` on the client.

Important rule:

- only one Watson-managed receive operation is allowed at a time per session/client

### 6. The client is async-first, not event-first

`WatsonWebSocketClient` does not start a hidden background message pump for you. You connect explicitly, then either:

- call `ReceiveAsync()` when you want one message
- use `ReadMessagesAsync()` when you want a continuous stream

This is an intentional shift away from the old `MessageReceived` callback model.

### 7. Raw client socket access exists, but it is an escape hatch

`WatsonWebSocketClient.RawSocket` exposes the underlying `ClientWebSocket` for advanced scenarios.

Use it sparingly:

- raw sends can coexist with Watson-managed receives
- mixing raw receives with Watson-managed `ReceiveAsync()` / `ReadMessagesAsync()` on the same connection is unsupported

### 8. Invalid certificate acceptance is explicit opt-in

If you used permissive TLS behavior before, you must now opt in through:

- `WebSocketClientSettings.AcceptInvalidCertificates = true`

That behavior is no longer the default.

## Before And After

### Connected / disconnected event model

WatsonWebsocket-style:

```csharp
server.ClientConnected += (sender, args) =>
{
    Console.WriteLine(args.Client.Guid);
};

server.ClientDisconnected += (sender, args) =>
{
    Console.WriteLine(args.Client.Guid);
};
```

Watson 7:

```csharp
server.Events.WebSocketSessionStarted += (sender, args) =>
{
    Console.WriteLine(args.Session.Id);
};

server.Events.WebSocketSessionEnded += (sender, args) =>
{
    Console.WriteLine(args.Session.Id);
};
```

### Message handling on the server

WatsonWebsocket-style:

```csharp
server.MessageReceived += async (sender, args) =>
{
    await server.SendAsync(args.Client.Guid, "echo:" + args.DataAsString);
};
```

Watson 7:

```csharp
server.WebSocket("/echo", async (ctx, session) =>
{
    await foreach (WebSocketMessage message in session.ReadMessagesAsync(ctx.Token))
    {
        await session.SendTextAsync("echo:" + message.Text, ctx.Token);
    }
});
```

### Session enumeration and disconnect

WatsonWebsocket-style:

```csharp
var clients = server.ListClients();
bool connected = server.IsClientConnected(guid);
await server.DisconnectClient(guid);
```

Watson 7:

```csharp
IEnumerable<WebSocketSession> sessions = server.ListWebSocketSessions();
bool connected = server.IsWebSocketSessionConnected(guid);
await server.DisconnectWebSocketSessionAsync(
    guid,
    WebSocketCloseStatus.NormalClosure,
    "disconnect");
```

### Client connect / send / receive

WatsonWebsocket-style:

```csharp
WatsonWsClient client = new WatsonWsClient("127.0.0.1", 8181, false);
client.MessageReceived += (sender, args) =>
{
    Console.WriteLine(args.DataAsString);
};

await client.StartAsync();
await client.SendAsync("hello");
```

Watson 7:

```csharp
WebSocketClientSettings settings = new WebSocketClientSettings();

using WatsonWebSocketClient client = new WatsonWebSocketClient(
    new Uri("ws://127.0.0.1:8181/ws/echo"),
    settings);

await client.ConnectAsync();
await client.SendTextAsync("hello");

WebSocketMessage reply = await client.ReceiveAsync();
Console.WriteLine(reply.Text);

await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done");
```

### Client connection settings and GUID header

Watson 7 moves client connection settings into `WebSocketClientSettings`:

```csharp
WebSocketClientSettings settings = new WebSocketClientSettings();
settings.Headers["x-tenant-id"] = "123";
settings.RequestedSubprotocols.Add("chat");
settings.ClientGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
settings.ClientGuidHeaderName = "x-guid";
settings.AcceptInvalidCertificates = true;
```

## HTTP Fallback Patterns

In WatsonWebsocket, some applications used raw HTTP fallback patterns next to websocket handling.

In Watson 7, use normal Watson HTTP routes for that behavior:

```csharp
server.Get("/chat", async req => new { Transport = "http" });
server.WebSocket("/chat", HandleSocketAsync);
```

## Unsupported Or Intentionally Changed Patterns

These patterns are intentionally different in Watson 7:

- no primary global per-message callback model on either server or client
- no public raw server-side websocket escape hatch
- no HTTP/2 or HTTP/3 websocket runtime yet
- no public server-side subprotocol negotiation surface yet
- no v1 `SendAndWaitAsync()` helper on the client

## Most Common Migrations

- WatsonWebsocket server only: install `Watson`, move websocket registration into `server.WebSocket(...)`, and move message handling into route handlers
- WatsonWebsocket client only: install `Watson.Clients`, replace `WatsonWsClient` with `WatsonWebSocketClient`, and replace callbacks with explicit receive calls or an async receive loop
- WatsonWebsocket server and client in one process: reference both packages and keep the roles separate

## Recommended Migration Steps

1. Decide whether the process is acting as a websocket server, a websocket client, or both.
2. Install `Watson` for server behavior and `Watson.Clients` for outbound client behavior.
3. Move websocket endpoint registration into `server.WebSocket(...)`.
4. Move server message processing logic into route handlers using `ReceiveAsync()` or `ReadMessagesAsync()`.
5. Replace old client connection code with `WatsonWebSocketClient` plus `WebSocketClientSettings`.
6. Replace old client-registry calls with Watson 7 session APIs.
7. Decide whether you need client-supplied GUID compatibility and explicitly enable it on both sides if required.
8. Keep observability concerns on `server.Events`, not in the application message path.
9. Use `RawSocket` only for advanced client scenarios where `ClientWebSocket` access is genuinely required.
