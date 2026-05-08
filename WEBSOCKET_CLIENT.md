# WebSocket Client Integration Plan

This document defines the recommended plan for bringing a WebSocket client into the Watson 7 repository after the server-side WatsonWebsocket merge.

It is intentionally opinionated. The goal is not to mechanically transplant `WatsonWsClient.cs`; the goal is to ship a client that fits Watson 7's architecture, gives archived WatsonWebsocket users a clear migration target, and does not weaken the current server package.

## How To Use This Document

Work top to bottom.

For each checklist item:
- change `[ ]` to `[x]` when complete
- change `[ ]` to `[~]` when partially complete
- change `[ ]` to `[>]` when intentionally deferred
- change `[ ]` to `[-]` when replaced by a different implementation
- add a short note below the item when implementation or validation differs

Suggested annotation style:

```md
- [x] Add async-first core client type
  Note: Implemented in `src/Watson.Clients/WatsonWebSocketClient.cs`
  Validation: `Test.XUnit.WatsonWebSocketClientCoverageTests.ConnectSendReceive`
```

## Recommendation Summary

Implement the client in this repository as a sibling library and package, not inside the `Watson` server assembly.

The recommended structure is:
- a new client project and package
- a primary public type named `WatsonWebSocketClient`
- a modern async-first API
- broad client target frameworks where practical so archived WatsonWebsocket users still have a viable migration path

The recommended package split is:
- `Watson` remains the server package
- `Watson.Clients` becomes the client package hosted in this repo

## Why This Approach

This plan is based on the current codebase shape:
- Watson 7 WebSocket support is explicitly server-side and route/session-oriented
- the server surface is already coherent and should not be reshaped around old client assumptions
- the old client contains behavior that should not be copied forward unchanged
- the old client supported frameworks that the current server package does not target

Specific issues in the old client that should not define the new architecture:
- implicit background receive loop as the primary model
- `SendAndWaitAsync()` that consumes the next inbound message globally rather than a correlated reply
- double-disconnect risk from multiple teardown paths
- broad exception swallowing in send paths
- invalid certificate handling that is not wired at the correct point for modern TFMs

## Goal

Integrate a WebSocket client into the Watson 7 repo while:
- preserving the clarity of the current Watson 7 server architecture
- giving WatsonWebsocket users a migration destination
- preserving whole-message semantics
- improving lifecycle correctness over the archived client
- keeping packaging and framework targeting appropriate for client-only consumers

## Delivery Strategy

### v1 Release Gate

v1 ships:
- a new client project in this repo
- a separately packable client package
- a separately packable symbol package
- `WatsonWebSocketClient` as the primary public client type
- an async-first client API
- interop coverage against the Watson 7 server
- zero XML-doc warnings in the client package build
- zero build warnings in the client package build
- docs covering client install, usage, migration, and intentional behavioral differences

### v1.x Follow-Up

v1.x may add:
- reconnect helpers
- a higher-level request/reply helper with correlation semantics
- browser/WASM-specific polish
- additional transport and proxy options if needed

## Scope

### In Scope For v1

- client-side websocket library hosted in this repo
- separate client package identity
- async-first connect, send, receive, and close APIs
- a raw public `ClientWebSocket` escape hatch
- whole-message delivery
- text and binary support
- headers, cookies, requested subprotocols, and GUID-header support
- connection state and counters
- shared automated and xUnit coverage
- documentation and migration guidance

### Explicitly Out Of Scope For v1

- moving the client directly into the `Watson` server package
- automatic reconnect as a core invariant
- RPC or correlation protocol design
- compression negotiation work
- HTTP/2 or HTTP/3-specific client feature surfacing beyond what `ClientWebSocket` already provides implicitly
- preserving every old behavior if the old behavior was incorrect, unsafe, or architecturally awkward

## Key Decisions

These should be treated as defaults unless a concrete implementation blocker requires deviation.

### 1. Packaging

Recommended:
- add a new project under `src/Watson.Clients/`
- produce a separate NuGet package named `Watson.Clients`
- keep `Watson` as the server package only

Reasoning:
- server and client have different audiences
- client runtime targets should not be constrained by server runtime targets
- shipping the client separately keeps the `Watson` package focused and avoids forcing a webserver package on client-only consumers

### 2. Namespace Strategy

Recommended:
- core client types live under `Watson.Clients`
- the primary public client type is `WatsonWebSocketClient`

Reasoning:
- new code gets a clearer, explicit client namespace
- the public type name is descriptive, searchable, and aligned with `.NET` `WebSocket` spelling

### 3. Framework Targeting

Recommended target set for the client project:
- `netstandard2.0`
- `netstandard2.1`
- `net462`
- `net48`
- `net8.0`
- `net10.0`

Reasoning:
- the old client supported broader TFMs than Watson 7 server
- archived WatsonWebsocket users are the users most likely to need the broader target set
- a separate client package allows broader TFMs without dragging the server package backward

If multi-target maintenance becomes materially painful, the first fallback is:
- drop `net462`

The last fallback is:
- align to `netstandard2.1;net8.0;net10.0`

Do not make that reduction the default starting point.

### 4. API Shape

Recommended:
- the public API is centered on a single async-first primary type
- the primary type is `WatsonWebSocketClient`

Primary API characteristics:
- explicit `ConnectAsync`
- explicit `ReceiveAsync` and `ReadMessagesAsync`
- one active receive path at a time
- no hidden background read loop
- no sync wrappers
- no compatibility shim as a release gate

### 5. Shared-Type Strategy

Recommended:
- do not introduce a third public package unless more than a few public shared types genuinely justify it
- keep client message and statistics types parallel to the server where necessary

Reasoning:
- a third package creates packaging and versioning overhead
- the server/client overlap is small enough that parallel types are acceptable if they are kept intentionally aligned

Decision rule:
- if implementation requires sharing more than three public WebSocket types, reconsider and extract a shared package
- otherwise prefer separate client-local types with matching semantics

### 6. Security Defaults

Recommended:
- invalid certificate acceptance is opt-in, not default-on

This is an intentional break from WatsonWebsocket 4.x behavior.

Reasoning:
- the old default is unsafe
- Watson 7 should not reintroduce insecure defaults for the sake of mechanical migration convenience

### 7. Raw Socket Exposure

Recommended:
- expose a raw public `ClientWebSocket` escape hatch
- keep the higher-level API as the primary programming model
- document the ownership boundary clearly
- option customization still happens through settings or a pre-connect callback

Reasoning:
- some advanced client scenarios need direct access to framework features or edge-case behaviors
- the client case is more defensible than the server case for a raw escape hatch
- the cost is acceptable if the invariants and tradeoffs are explicit

## Target Architecture

### Core Client Layer

Add a core type named `WatsonWebSocketClient` that owns:
- URI and connection settings
- connect/disconnect lifecycle
- whole-message receive
- send serialization
- state and close tracking
- counters
- cancellation and disposal coordination

The core client should feel like the client-side analogue of `WebSocketSession`, not like a port of the old event loop.

Raw socket exposure should be treated as an advanced escape hatch, not the default interaction style.

## Raw Escape Hatch Policy

The raw `ClientWebSocket` escape hatch is in scope, but it must be explicitly bounded.

Recommended rules:
- expose the underlying socket through a clearly named member such as `RawSocket` or `UnderlyingClientWebSocket`
- document whether the socket is null before connect and after disposal
- document that raw sends and receives can bypass Watson-managed counters and serialization unless explicitly integrated
- document that mixing Watson-managed receive APIs with raw `ReceiveAsync` on the same connection is unsupported
- document whether raw send usage is supported concurrently with Watson-managed send APIs or intentionally unsupported

Recommended default rule:
- once a caller performs raw receive operations, that caller owns receive coordination for the lifetime of that connection unless the implementation explicitly supports handoff

This feature should be documented as:
- advanced
- powerful
- easy to misuse if mixed with the higher-level receive path without discipline

## Target Public Surface

Use this surface as the planning baseline.

### Core Client Type

Recommended core type name:
- `WatsonWebSocketClient`

Recommended construction shape:
- constructor taking `Uri`
- constructor or settings object support for advanced configuration

Recommended core members:
- `Uri ServerUri`
- `bool IsConnected`
- `WebSocketState State`
- `WebSocketCloseStatus? CloseStatus`
- `string CloseStatusDescription`
- `string Subprotocol`
- `ClientWebSocket RawSocket` or equivalent clearly named escape hatch
- `WebSocketClientStatistics Statistics`
- `Task ConnectAsync(CancellationToken token = default)`
- `Task<WebSocketMessage> ReceiveAsync(CancellationToken token = default)`
- `IAsyncEnumerable<WebSocketMessage> ReadMessagesAsync(CancellationToken token = default)`
- `Task SendTextAsync(string data, CancellationToken token = default)`
- `Task SendBinaryAsync(byte[] data, CancellationToken token = default)`
- `Task SendBinaryAsync(ArraySegment<byte> data, CancellationToken token = default)`
- `Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken token = default)`

Recommended supporting configuration:
- settings object or builder for headers, cookies, keepalive, requested subprotocols, certificate behavior, GUID header, and option customization

## Behavioral Rules

These rules should be treated as non-negotiable unless a design note documents a deviation.

### Connect

- connect is explicit
- connect failure should surface as an exception in the core API
- timeout behavior in the core API should come from cancellation tokens, not special timeout-only methods
- option customization and certificate callbacks must be applied before connect begins

### Receive

- whole-message receive only
- exactly one active receive operation at a time
- fragmented frames are reassembled before delivery
- no hidden background receive

### Send

- sends are serialized
- successful sends increment counters
- send failures are not silently swallowed in the core layer

### Close And Dispose

- close is idempotent
- disconnect notification should not fire twice
- state and close reason should remain available after shutdown
- disposal should cleanly cancel pending operations

### Certificate Handling

- invalid certificate acceptance is opt-in
- implementation must wire validation at the correct point before connect
- tests must prove both strict and opt-in acceptance paths

## Compatibility Mapping

Map the old archived client deliberately, not mechanically.

### Carry Forward As First-Class Features

- constructor from `Uri`
- custom GUID header support
- cookie support
- pre-connect option customization
- text and binary send helpers
- basic counters

### Reinterpret Cleanly In The New API

- `StartAsync()` becomes `ConnectAsync()`
- `StopAsync()` becomes `CloseAsync()` or disposal
- `Connected` becomes `IsConnected`
- `MessageReceived` becomes `ReceiveAsync()` / `ReadMessagesAsync()`
- host-and-port convenience may exist only if it is still a clean API, not because the old client had it

### Do Not Carry Forward Into v1

- event-driven message receipt as the only receive model
- `SendAndWaitAsync()`
- sync wrappers
- hidden background receive as the main programming model
- permissive invalid-certificate default
- exception swallowing in send paths

## Repository Layout

Recommended new files and folders:

- `WEBSOCKET_CLIENT.md`
- `src/Watson.Clients/Watson.Clients.csproj`
- `src/Watson.Clients/WatsonWebSocketClient.cs`
- `src/Watson.Clients/WebSocketClientSettings.cs`
- `src/Watson.Clients/WebSocketClientStatistics.cs`
- `src/Watson.Clients/WebSocketMessage.cs`
- `src/Watson.Clients/WebSocketClientConnectionInfo.cs` if needed

Recommended existing files expected to change:

- `src/WatsonWebserver.sln`
- `src/Test.WebsocketClient/Program.cs`
- `src/Test.Shared/...`
- `src/Test.XUnit/...`
- `src/Test.Automated/...`
- `README.md`
- `CHANGELOG.md`
- `MIGRATING_FROM_WATSONWEBSOCKET.md`
- optionally a new `WEBSOCKET_CLIENT_API.md`

## Implementation Phases

## Phase 0: Decision Freeze And Scaffolding

### 0.1 Package Identity

- [x] Confirm client package name `Watson.Clients`
- [x] Confirm assembly name
- [x] Confirm namespace plan for primary and supporting types
- [x] Confirm primary public type name `WatsonWebSocketClient`
- [x] Confirm client versioning strategy relative to the `Watson` server package
  Note: Implemented in `src/Watson.Clients/Watson.Clients.csproj` and `src/Watson.Clients/*.cs`.
  Note: Current versioning is aligned to the server package at `7.0.14`.

Implementation note:
- the client package does not need to share the same major version as `Watson` if that creates unnecessary friction
- it does need clear repo-level release notes and discoverability

### 0.2 Project Setup

- [x] Add the new client project under `src/`
- [x] Add it to `src/WatsonWebserver.sln`
- [x] Configure package metadata, README packing, license packing, XML docs, and symbols
- [x] Configure target frameworks
- [x] Ensure CI/build steps pack the client package separately
  Note: `GeneratePackageOnBuild` now produces `Watson.Clients` artifacts from the normal `Release` build.

### 0.2.1 Client `csproj` Packaging And Warning Gates

- [x] Set `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` in `src/Watson.Clients/Watson.Clients.csproj`
- [x] Set `<IncludeSymbols>true</IncludeSymbols>`
- [x] Set `<SymbolPackageFormat>snupkg</SymbolPackageFormat>`
- [x] Set `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
- [x] Set an explicit `<DocumentationFile>` path such as `Watson.Clients.xml`
- [x] Set package metadata: `PackageId`, `Title`, `Description`, `Authors`, `Company`, `Product`, `Copyright`
- [x] Set repository metadata: `PackageProjectUrl`, `RepositoryUrl`, `RepositoryType`
- [x] Set package asset metadata: `PackageLicenseFile`, `PackageReadmeFile`, `PackageIcon`, `PackageTags`
- [x] Pack `README.md` into the package root
- [x] Pack `LICENSE.md` into the package
- [x] Pack the icon asset into the package
- [x] Ensure XML documentation output is generated consistently for every target framework
- [x] Ensure package generation works from a clean `Release` build

### 0.2.2 Zero-Warning Policy

- [x] Add a client-project warning gate such as `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` or an equivalent CI-enforced `/warnaserror` policy
- [x] Eliminate XML documentation warnings on the client package project
- [x] Eliminate ordinary compiler/build warnings on the client package project
- [x] Eliminate warnings on any touched server or test projects
- [x] Do not use blanket `NoWarn` suppression to hide XML-doc or build warnings
- [-] If a warning cannot be eliminated, add a narrowly scoped suppression with a note in this plan explaining why
  Note: No scoped suppressions were required.

Required outcome:
- the client package project builds warning-free across all target frameworks
- the solution slice touched by this work builds warning-free in `Release`

### 0.2.3 Build And Pack Verification Commands

- [x] Verify `dotnet build src/Watson.Clients/Watson.Clients.csproj -c Release /warnaserror`
- [x] Verify `dotnet pack src/Watson.Clients/Watson.Clients.csproj -c Release`
- [x] Verify the `.nupkg` artifact is produced
- [x] Verify the `.snupkg` artifact is produced
- [x] Verify package contents include README, license, icon, and XML docs where intended
- [x] Verify package metadata renders correctly in the NuGet artifact
  Validation: `dotnet build src\Watson.Clients\Watson.Clients.csproj -c Release /warnaserror`
  Validation: `dotnet pack src\Watson.Clients\Watson.Clients.csproj -c Release --no-build`
  Validation: inspected `src/Watson.Clients/bin/Release/Watson.Clients.7.0.14.nupkg`

### 0.3 Repo Integration

- [x] Add top-level references to the new client docs from `README.md`
- [x] Add changelog placeholder entries
- [x] Add test-project references needed for shared coverage

## Phase 1: Core API And Type Model

### 1.1 Settings Model

- [x] Add `WebSocketClientSettings`
- [x] Add URI configuration rules
- [x] Add header configuration support
- [x] Add cookie configuration support
- [x] Add requested subprotocol support
- [x] Add keepalive configuration
- [x] Add invalid-certificate opt-in
- [x] Add client GUID and GUID-header configuration
- [x] Add pre-connect `ClientWebSocketOptions` customization callback

Required behavior:
- settings must be immutable after connect begins, or copied into a connection snapshot before connect

### 1.2 Message And Statistics Types

- [x] Add client-side `WebSocketMessage`
- [x] Add client-side `WebSocketClientStatistics`
- [x] Keep message semantics aligned with server-side `WebSocketMessage`
- [x] Keep stats semantics aligned with server-side session stats where practical

### 1.3 Core Client State Model

- [x] Define connection lifecycle states to expose
- [x] Add close-status tracking
- [x] Add subprotocol tracking
- [x] Add server-endpoint tracking
- [x] Define raw-socket availability rules across unconnected, connected, closing, and disposed states
- [x] Add deterministic disposal rules

Required behavior:
- connected state must not depend on a hidden background task continuing to run

## Phase 2: Core Runtime Implementation

### 2.1 Connect Path

- [x] Build the `ClientWebSocket` instance per connection attempt
- [x] Apply all settings before `ConnectAsync`
- [x] Apply certificate validation before `ConnectAsync`
- [x] Apply headers, cookies, subprotocols, and GUID header before `ConnectAsync`
- [x] Record negotiated subprotocol and final state after connect

### 2.2 Receive Path

- [x] Implement whole-message `ReceiveAsync`
- [x] Implement `ReadMessagesAsync`
- [x] Reassemble fragmented messages
- [x] Reject concurrent receives
- [x] Update counters on successful receive
- [x] Handle close frames cleanly

### 2.3 Send Path

- [x] Add per-client send serialization
- [x] Add text and binary send helpers
- [x] Update counters on successful send
- [x] Ensure send failures are surfaced correctly
- [x] Ensure send does not silently succeed after a swallowed exception

### 2.4 Close And Disposal

- [x] Implement graceful close
- [x] Preserve final close state after close/dispose
- [x] Make close/dispose idempotent
- [x] Ensure pending receive/send operations are canceled cleanly
- [x] Ensure no duplicate disconnect completion path exists

### 2.5 Quality Bars

- [x] Do not use `ContinueWith`-driven lifecycle plumbing in the core client
- [x] Do not swallow broad send exceptions and then report success
- [x] Do not emit duplicate disconnect completion
- [x] Do not install certificate validation too late
- [x] Do not start hidden receive tasks in the core API
- [x] Do not leave raw-socket ownership semantics ambiguous

## Phase 3: Surface Finalization And Convenience Decisions

### 3.1 Primary Type Surface

- [x] Add primary public type `WatsonWebSocketClient`
- [x] Finalize constructor shape
- [x] Decide whether to expose host-and-port convenience construction
- [x] Finalize the raw-socket member name and shape
- [x] Ensure all public names use `.NET` `WebSocket` spelling consistently

### 3.2 Optional Convenience APIs

- [x] Decide whether connection lifecycle events belong in v1
- [-] If events ship, ensure ordering is deterministic
- [-] If events ship, ensure disconnect completion fires at most once
- [x] Decide whether timeout-oriented helper methods belong in v1
- [x] Do not add sync wrappers in v1
- [x] Do not add `SendAndWaitAsync()` in v1
- [x] Define and document mixing rules between raw-socket use and Watson-managed APIs
  Note: v1 intentionally ships without client lifecycle events and without timeout helper wrappers.

If timeout helpers ship:
- they must be built on cancellation and retry logic that does not leak sockets or background tasks

## Phase 4: Manual Harnesses And Samples

### 4.1 Interactive Client Harness

- [x] Convert `src/Test.WebsocketClient/Program.cs` from raw `ClientWebSocket` usage to the new client package
- [x] Preserve header, subprotocol, connect, send, close, and receive inspection workflows

### 4.2 Sample Server Interop

- [x] Keep `src/Test.WebsocketServer/Program.cs` as the primary manual Watson7 peer
- [~] Validate client interop against echo, time, upper, inspect, broadcast, and close routes
  Note: the updated interactive client keeps presets for these routes; no single recorded manual walkthrough covered all of them yet.

## Phase 5: Automated Test Coverage

The automated coverage bar should be at least as serious as the existing Watson 7 server coverage.

Client coverage is a release-gate surface and should be treated as exhaustive.

Server coverage is also a release gate. The addition of a client package must not weaken the current Watson 7 websocket server test posture.

### 5.0 Coverage Standard

- [~] Treat client automated coverage as exhaustive for the shipped v1 surface
- [x] Treat server websocket regression coverage as mandatory for release
- [~] Ensure every public client API member is exercised by at least one automated test
- [~] Ensure every documented client limitation is either asserted by tests or explicitly marked as manual-only with justification
- [~] Ensure positive-path, negative-path, cancellation, concurrency, shutdown, cleanup, and lifecycle coverage exist for each major client feature area
- [x] Ensure raw-socket escape-hatch behavior is tested anywhere the docs promise support
- [ ] Ensure unsupported mixed-ownership patterns fail or block exactly as documented
  Note: the current suite is broad and release-grade, but the deferred edge cases below are not all implemented yet.

### 5.1 Shared Test Architecture

- [x] Add canonical client scenarios to `Test.Shared`
- [x] Reuse existing loopback server helpers where practical
- [x] Surface each canonical client scenario in both `Test.XUnit` and `Test.Automated`
- [x] Keep existing websocket server canonical scenarios green
- [x] Add at least one client-driven regression layer that exercises the Watson 7 server using `WatsonWebSocketClient`, not only raw `ClientWebSocket`

### 5.1.1 Server Regression Requirements

- [x] Existing websocket server tests remain passing after client integration work
- [x] Same-path HTTP and websocket routing still passes with the existing server test suite
- [x] Session enumeration and disconnect-by-guid remain covered
- [x] Handshake failure and close-path coverage remain intact
- [x] Server-side websocket statistics coverage remains intact
- [x] At least one suite verifies Watson 7 server interop using `WatsonWebSocketClient`
- [x] At least one suite verifies Watson 7 server interop using the raw `ClientWebSocket` escape hatch where supported
- [x] Raw client usage must not mask server regressions that the higher-level client surface would expose
  Validation: `dotnet test src\Test.XUnit\Test.XUnit.csproj -c Release --filter "SharedCoreUnitCasePasses"`
  Validation: `dotnet run --project src\Test.Automated\Test.Automated.csproj -c Release --framework net10.0 --no-build`

### 5.2 Core Client Scenarios

- [x] connect to Watson7 server over `ws`
- [x] connect to Watson7 server over `wss`
- [x] connect with invalid TLS certificate fails by default
- [x] connect with invalid TLS certificate succeeds only when explicitly allowed
- [ ] connect cancellation before socket establishment is handled cleanly
- [ ] connect cancellation during handshake is handled cleanly
- [x] reconnect after a prior clean close works
- [ ] reconnect after a failed connection attempt works
- [x] send text to Watson7 server
- [x] send binary to Watson7 server
- [ ] zero-length text payload behavior is explicitly tested and documented
- [ ] zero-length binary payload behavior is explicitly tested and documented
- [x] receive text from Watson7 server
- [x] receive binary from Watson7 server
- [x] request/reply using explicit receive calls
- [ ] fragmented message assembly
- [ ] large payload near limit
- [ ] oversized payload behavior is explicitly tested and documented
- [ ] UTF-8 text integrity is preserved
- [x] binary payload integrity is preserved
- [x] server-initiated close
- [x] client-initiated close
- [x] remote close status and description are retained
- [ ] abrupt server stop
- [ ] abrupt server abort/disconnect is handled cleanly
- [ ] cancellation during connect
- [x] cancellation during receive
- [ ] cancellation during send
- [ ] cancellation during close
- [ ] concurrent sends remain serialized
- [x] concurrent receives are rejected
- [ ] concurrent send and receive works when supported
- [x] close state retained after shutdown
- [x] stats advance correctly
- [x] raw-socket availability matches the documented lifecycle
- [ ] double close is safe and deterministic
- [x] double dispose is safe and deterministic
- [ ] send after close behavior is explicitly tested and documented
- [ ] receive after close behavior is explicitly tested and documented
- [x] dispose before connect is safe
- [ ] dispose during receive is safe
- [ ] dispose during send is safe

### 5.3 Configuration Scenarios

- [x] URI validation rejects invalid websocket schemes
- [ ] URI validation handles paths and querystrings correctly
- [x] host-and-port convenience construction works if it ships
- [x] custom headers are sent
- [x] cookies are sent
- [x] requested subprotocols are sent
- [ ] negotiated subprotocol is surfaced correctly
- [x] GUID header default matches Watson7 default behavior
- [x] custom GUID header name works
- [x] invalid certificate rejection works by default
- [x] invalid certificate acceptance works when explicitly enabled
- [x] pre-connect `ClientWebSocketOptions` customization is applied
- [ ] keepalive configuration is applied
- [x] raw-socket member naming and nullability semantics match docs

### 5.4 Surface And Convenience Scenarios

- [x] primary type `WatsonWebSocketClient` is constructible through the chosen entry points
- [x] URI validation behavior is correct
- [x] host-and-port convenience construction works if it ships
- [-] timeout-oriented helpers work if they ship
- [-] optional lifecycle events work if they ship
- [x] raw-socket member is null or unavailable when documented to be unavailable
- [x] raw-socket member exposes the active `ClientWebSocket` when connected
- [x] raw send usage interoperates with Watson7 server when documented as supported
- [x] raw receive usage interoperates with Watson7 server when documented as supported
- [ ] raw close usage interoperates with Watson7 server when documented as supported
- [ ] mixed raw and Watson-managed receive usage fails or is blocked exactly as documented
- [ ] mixed raw and Watson-managed send usage behaves exactly as documented
- [ ] mixed raw and Watson-managed close behavior is exactly as documented

### 5.5 Interop Beyond Watson7

- [>] Add at least one non-Watson interop path if practical
- [>] Add browser/WASM validation if practical

If third-party interop is not automated in v1:
- record at least one manual validation path and the reason automation was deferred

### 5.6 Resource And Regression Coverage

- [ ] long-running repeated connect/send/receive/close cycles do not leak state
- [ ] repeated connect/disconnect cycles do not leak state
- [ ] retry-based connect helpers do not leak abandoned `ClientWebSocket` instances
- [-] no duplicate disconnect event emission exists
- [x] no background receive tasks remain after shutdown
- [ ] no hidden race exists between close and receive completion
- [x] raw-socket access does not leave disposed-socket state ambiguous
- [ ] counters remain correct under repeated cycles
- [x] no test leaves a socket open or a server session orphaned on completion
  Note: v1 does not ship disconnect events on the client surface.

### 5.7 Build And Packaging Validation Tests

- [x] Add automated verification that the client project builds in `Release` without warnings
- [x] Add automated verification that the client package packs successfully
- [x] Add automated verification that the symbol package packs successfully
- [x] Add automated verification that XML documentation output is generated
- [x] Add automated verification that package artifacts contain the intended files

## Phase 6: Documentation And Migration

### 6.1 Client API Guide

- [x] Create `WEBSOCKET_CLIENT_API.md` or equivalent focused client doc
- [x] Document install guidance
- [x] Document `WatsonWebSocketClient`
- [x] Document async-first usage
- [x] Document the raw `ClientWebSocket` escape hatch and its invariants
- [x] Document headers, cookies, subprotocols, and GUID-header support
- [x] Document TLS and certificate options
- [x] Document intentional limitations
  Note: implemented as `src/Watson.Clients/README.md` rather than a separate `WEBSOCKET_CLIENT_API.md`.

### 6.2 README

- [x] Add a client section to `README.md`
- [x] Clearly distinguish server-side WebSocket support from the new client package
- [x] Introduce `WatsonWebSocketClient` as the primary client API
- [x] Link to the dedicated client doc and migration guide

### 6.3 Migration Guide

- [x] Extend `MIGRATING_FROM_WATSONWEBSOCKET.md` or create a client-specific migration doc
- [x] Map old `WatsonWsClient` concepts to `WatsonWebSocketClient`
- [x] Call out intentional behavioral changes
- [x] Call out security-default changes
- [x] Call out dropped APIs such as `SendAndWaitAsync()` if they do not ship
- [x] Call out the new raw-socket escape hatch if it did not exist as a formal supported surface before

### 6.3.1 `MIGRATING_FROM_WATSONWEBSOCKET.md` Client Update Tasks

- [x] Update the migration guide scope statement so it explicitly covers both server and client migration
- [x] Add a short section near the top explaining that WatsonWebsocket server functionality moved into `Watson`, while client functionality now lives in `Watson.Clients`
- [x] Add install/package guidance for client users, including the `Watson.Clients` package name
- [x] Add a client concept-mapping table from `WatsonWsClient` to `WatsonWebSocketClient`
- [x] Add a method/property mapping table for the old client surface to the new client surface
- [x] Add a section describing the new primary programming model: explicit `ConnectAsync`, `ReceiveAsync`, and `ReadMessagesAsync`
- [x] Add before-and-after code examples for basic client connect/send/receive/close usage
- [x] Add migration guidance for headers, cookies, requested subprotocols, and GUID-header behavior
- [x] Add migration guidance for TLS and invalid-certificate behavior, explicitly calling out the default change
- [x] Add migration guidance for the raw `ClientWebSocket` escape hatch, including when it should and should not be used
- [x] Add a section enumerating intentionally dropped or changed client APIs such as sync wrappers or `SendAndWaitAsync()` if they do not ship
- [-] Add a section explaining any event-model changes if optional lifecycle events ship
- [ ] Add a short “most common migrations” section for archived WatsonWebsocket users
- [x] Add links from the migration guide to `README.md` and `WEBSOCKET_CLIENT_API.md`
- [x] Update any server-only language in `MIGRATING_FROM_WATSONWEBSOCKET.md` that would become misleading once client migration is documented
  Note: the focused client-guide link targets `src/Watson.Clients/README.md` rather than a separate `WEBSOCKET_CLIENT_API.md`.

Required migration-guide outputs:
- one clear package-selection explanation
- one clear client API mapping table
- one clear before-and-after client example
- one explicit list of intentional client-surface changes

Required migration notes:
- invalid certificate acceptance is no longer default-on
- the preferred receive model is explicit and async-first
- the old event-driven model is not the primary Watson 7 client API

### 6.4 Changelog

- [x] Add a changelog entry for the new client package
- [x] Add a changelog entry for any server-doc changes needed to reference the client package

## Phase 7: Release And Deprecation Cleanup

### 7.1 Packaging

- [x] Verify client package packs independently
- [x] Verify symbol package packs independently
- [x] Verify symbols and XML docs are included
- [x] Verify package README and license are correct
- [x] Verify package build is warning-free
- [x] Verify pack is warning-free

### 7.2 Discoverability

- [x] Point archived WatsonWebsocket migration guidance at the new client package
- [x] Ensure README and package metadata make the split obvious

### 7.3 Final Review

- [x] Verify server package remains server-focused
- [x] Verify client package can be consumed without the server package
- [x] Verify `WatsonWebSocketClient` is the clear primary public surface

## Review Checklist

- [x] package split confirmed
- [x] framework targets confirmed
- [x] client `csproj` is prepared to emit `.nupkg` and `.snupkg`
- [x] client `csproj` emits XML docs cleanly
- [x] zero XML-doc warnings verified
- [x] zero build warnings verified
- [x] async-first core API reviewed
- [x] primary type naming reviewed
- [x] raw-socket escape hatch reviewed
- [x] invalid-certificate default reviewed
- [x] manual client harness updated
- [x] shared tests added
- [x] xUnit exposure added
- [x] automated runner exposure added
- [x] websocket server regression coverage remains adequate
- [~] client websocket coverage is exhaustive for the shipped surface
- [x] README updated
- [x] migration guidance updated
- [x] release notes updated

## Deliverables

- [x] `Watson.Clients` project
- [x] `WatsonWebSocketClient`
- [x] async-first client API
- [x] raw public `ClientWebSocket` escape hatch
- [x] client NuGet package
- [x] client symbol package
- [x] zero-warning client build
- [x] manual client test harness updated to use the new package
- [x] shared test scenarios
- [x] xUnit coverage
- [x] automated-runner coverage
- [~] exhaustive automated client coverage
- [x] adequate websocket server regression coverage
- [x] client API documentation
- [x] migration documentation
- [x] changelog updates

## Progress Summary

- Design complete: [x]
- Project scaffolding complete: [x]
- Core API complete: [x]
- Core runtime complete: [x]
- Surface finalization complete: [x]
- Packaging and warning gates complete: [x]
- Manual harness complete: [x]
- Shared tests complete: [x]
- xUnit exposure complete: [x]
- automated-runner exposure complete: [x]
- Documentation complete: [x]
- Ready for release review: [x]

## Final Opinion

The best implementation path is not to paste `WatsonWsClient.cs` into Watson 7 and call it done.

The best path is:
- keep `Watson` server-only
- add a sibling client package in this repo
- make `WatsonWebSocketClient` the primary public type
- design the client around explicit async receive semantics

That gives Watson 7 a client story without compromising the server package, and it gives WatsonWebsocket users a clean upgrade target instead of a nominal merge.
