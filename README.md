# Centrifuge C# SDK

❗❗❗ This SDK is a work in progress, we do not recommend using it in the production ❗❗❗ At this point we’re looking for community feedback before evolving it further, since we don’t yet have a production use case driving its direction internally.

[![NuGet](https://img.shields.io/nuget/v/ImpulseM.Centrifuge.svg)](https://www.nuget.org/packages/ImpulseM.Centrifuge/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

C# client SDK for [Centrifugo](https://github.com/centrifugal/centrifugo) and [Centrifuge](https://github.com/centrifugal/centrifuge) real-time messaging servers.

This is the ImpulseM fork of [centrifuge-csharp](https://github.com/centrifugal/centrifuge-csharp). It keeps the upstream API and adds the features described in [What the fork adds](#what-the-fork-adds). The code namespace is still `Centrifugal.Centrifuge`; the NuGet package is `ImpulseM.Centrifuge`.

## Features

- ✅ Multi-platform support
- ✅ WebSocket and HTTP-streaming transports with automatic fallback
- ✅ Browser-native transports for Blazor WebAssembly (using JS interop)
- ✅ Protobuf binary protocol for efficient communication
- ✅ Automatic command batching for improved network efficiency
- ✅ Automatic reconnection with exponential backoff and full jitter
- ✅ Channel subscriptions with recovery and Fossil delta compression support
- ✅ Server-side publication filtering based on tags
- ✅ JWT authentication with automatic token refresh
- ✅ Publications and RPC calls
- ✅ Presence and presence stats, join/leave events
- ✅ History API
- ✅ Thread-safe
- ✅ Per-call RPC headers, cancellable calls and exactly-once calls with a connection instance
- ✅ RPC pool for large payloads, over several connections per endpoint
- ✅ Token exchange over the connection, with a refresh token the application persists
- ✅ Connection token refresh on demand, and the identity of the connection
- ✅ Publish debouncing, set by the server

## Platform Support

The library targets modern .NET frameworks:

- .NET 10.0
- .NET 9.0
- .NET 8.0
- .NET 6.0
- .NET Standard 2.1 (for Unity support)

This means it works on:

- ✅ **Server-side**: .NET 6/8/9/10
- ✅ **Desktop**: Windows, macOS, Linux (via modern .NET)
- ✅ **Mobile**: .NET MAUI (iOS, Android)
- ✅ **Web**: Blazor WebAssembly, Blazor Server
- ✅ **Unity**: 2021.2+ with .NET Standard 2.1 support

> **Note**: UWP is not recommended for new projects. Consider using Windows App SDK / WinUI 3 instead, which work with modern .NET targets.

## Transport Compatibility

The SDK supports multiple transport protocols with platform-specific implementations for optimal performance:

| Transport | Server/.NET Desktop | Blazor Server | Blazor WASM | Unity Desktop/Mobile | Unity WebGL |
|-----------|---------------------|---------------|-------------|----------------------|-------------|
| **WebSocket** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ⚠️ Plugin Required |
| **HTTP Stream** | ✅ Native | ✅ Native | ✅ JS Interop* | ✅ Native | ❌ Not Supported |

**\*Blazor WebAssembly**: Requires `builder.Services.AddCentrifugeClient()` in `Program.cs`. The SDK automatically uses browser-native implementations via JavaScript interop for both WebSocket and HTTP Stream transports.

**Unity WebGL**: Requires a third-party WebSocket plugin. HTTP Stream is not supported in WebGL.

## Installation

Install via NuGet Package Manager. nuget.org is a public feed, so no account or token is needed:

```bash
dotnet add package ImpulseM.Centrifuge
```

Or via Package Manager Console:

```powershell
Install-Package ImpulseM.Centrifuge
```

The package carries the `LICENSE` and `THIRD_PARTY_NOTICES.md` files at its root, and its symbol package is published with it.

## Quick Start

### Basic Connection

```csharp
using Centrifugal.Centrifuge;

// Recommended: Use 'await using' for proper async disposal
await using var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

// Setup event handlers
client.Connecting += (sender, e) =>
{
    Console.WriteLine($"[Client] Connecting: {e.Code} - {e.Reason}");
};

client.Connected += (sender, e) =>
{
    Console.WriteLine($"Connected with client ID: {e.ClientId}");
};

client.Disconnected += (sender, e) =>
{
    Console.WriteLine($"Disconnected: {e.Code} - {e.Reason}");
};

client.Error += (sender, e) =>
{
    Console.WriteLine($"Error: {e.Message}");
};

// Connect to server (non-blocking)
client.Connect();

// Use client methods - they automatically wait for connection
var result = await client.RpcAsync("method", data);

// DisposeAsync is called automatically at the end of the 'await using' block
// It waits for disconnect to complete before releasing resources
```

## Event Handlers and Callbacks

### ⚠️ Important: Use Async Event Handlers

The state of a client and its subscriptions changes on one **event loop** per client: an application call, a frame received from the server, a timer or the result of a callback (`GetToken`, `GetState`) is one step, and steps run one at a time, in order. Event handlers run **synchronously** inside the step that raises them, so your application observes events in the order things happened: `Connected` (with the server-side subscription events and recovered publications it carries) and a subscription's `Subscribed` (followed by its recovered publications) are raised before any publication received after the corresponding reply, and a handler never runs concurrently with another handler of the same client.

The loop isn't a dedicated thread: a step runs on the thread that brings it (your thread for a call, a transport's or the thread pool's otherwise) when the loop is idle, or right after the step in progress. The SDK never blocks your thread waiting for the loop: a call made from another thread while a handler runs takes effect after that handler returns.

A call into the SDK from a handler (e.g. `sub.Unsubscribe()`, `client.Disconnect()`) takes effect at once, before it returns — as in centrifuge-js. For best performance and responsiveness, keep callbacks fast or use `async void` event handlers:

```csharp
client.Connected += async (sender, e) =>
{
    Console.WriteLine($"Connected: {e.ClientId}");
    await SaveAsync();
};
```

**Can you use blocking calls like `.Wait()` or `.Result`?**

**NO - This will cause a deadlock!** Never block on SDK async methods in event handlers:

```csharp
client.Connected += (sender, e) =>
{
    // ❌ DEADLOCK! Never use .Wait(), .Result, or .GetAwaiter().GetResult()
    client.PublishAsync("channel", data).Wait();          // DEADLOCK!
    var result = client.RpcAsync("method", data).Result;  // DEADLOCK!
};
```

**Why does this deadlock?** The handler runs inside a step of the client's loop. `PublishAsync()` completes when its reply is processed — by a later step of the same loop, which can't run while your handler blocks it.

While a handler runs, the client processes nothing else: no replies, no pings, no publications of other subscriptions, no timers. So any synchronous wait in a handler for something that needs the client deadlocks or stalls it — including marshalling synchronously (`Dispatcher.Invoke`, `Control.Invoke`, `SynchronizationContext.Send`) to a thread that waits for the SDK, or taking a lock that a thread calling the SDK may hold while it waits for the reply. A call from another thread made while a handler runs doesn't wait for it: it takes effect right after the handler returns, in call order — so don't read `State` right after such a call to learn its outcome; use the events or `ReadyAsync()`. Replies that arrive while a handler runs are processed after it returns; a reply already received when its timeout fires is still delivered. Keep handlers fast, and post to the UI thread asynchronously (`BeginInvoke`, `SynchronizationContext.Post`).

Calling `Disconnect()`, `Dispose()` or `DisposeAsync()` from a handler is safe: they take effect at once and don't wait for the connection to close.

**Signalling Your Code from Event Handlers**

A `TaskCompletionSource` that a handler completes should be created with `TaskCreationOptions.RunContinuationsAsynchronously`. Without it, the code awaiting its task continues inline, inside the handler: it holds the client's loop until it yields, and deadlocks if it blocks on an SDK method:

```csharp
var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
subscription.Subscribed += (sender, e) => subscribed.TrySetResult(true);
```

**Exception Handling in Event Handlers**

Event handlers should not throw: catch exceptions in your handlers. The SDK can't know what a throwing handler left undone, so it fails loudly instead of going on as if nothing happened:

* the exception is reported through the `Error` event of the client or subscription, with `Type` set to the name of the failing event (`publication`, `subscribing`, ...);
* the client disconnects with code `2` (bad protocol) and the reason `exception in event handler: <message>`, and doesn't reconnect: call `Connect()` to start again;
* the handlers after the failing one don't get that event; the SDK's own work completes as if the handler had returned, so the client is left in a consistent state;
* a publication whose handler threw doesn't count as delivered: after `Connect()`, a recoverable subscription receives it again.

An exception thrown by an `Error` handler is logged, and stops the client as well. An `async void` handler that throws after its first `await` is out of the SDK's reach, and the exception crashes the application. Catch exceptions yourself:

```csharp
// ❌ BAD: throws after the first await - crashes the application
subscription.Publication += async (sender, e) =>
{
    await riskyOperation();
};

// ✅ GOOD: Always wrap your logic in try-catch
subscription.Publication += async (sender, e) =>
{
    try
    {
        await riskyOperation();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error processing publication: {ex.Message}");
        // Log, report to error tracking service, etc.
    }
};
```

**Best Practices Summary**

1. ✅ Use `async void` for event handlers that need to perform async operations
2. ✅ Always `await` async methods - never block with `.Wait()`, `.Result`, or `.GetAwaiter().GetResult()`
3. ✅ Always wrap `async void` handlers in `try-catch` to prevent application crashes
4. ✅ Keep handlers fast - heavy CPU work should be offloaded to background tasks
5. ✅ Event order is preserved - handlers are invoked synchronously, one at a time, on the client's event loop
6. ✅ Pass `TaskCreationOptions.RunContinuationsAsynchronously` to any `TaskCompletionSource` a handler completes

## Subscriptions

```csharp
// Create subscription, may throw CentrifugeDuplicateSubscriptionException
// if subscription to the channel already exists in Client's registry.
var subscription = client.NewSubscription("chat");

// Setup subscription event handlers
subscription.Publication += (sender, e) =>
{
    var data = Encoding.UTF8.GetString(e.Data.Span);
    Console.WriteLine($"Message from {e.Channel}: {data}");
};

subscription.Subscribing += (sender, e) =>
{
    Console.WriteLine("Subscribing to channel");
};

subscription.Subscribed += (sender, e) =>
{
    Console.WriteLine($"Subscribed to channel, recovered: {e.Recovered}");
};

subscription.Unsubscribed += (sender, e) =>
{
    Console.WriteLine($"Unsubscribed: {e.Code} - {e.Reason}");
};

// Subscribe to channel (non-blocking)
subscription.Subscribe();

// Publish to channel - automatically waits for subscription to be ready
var message = Encoding.UTF8.GetBytes("Hello, world!");
await subscription.PublishAsync(message);

// Unsubscribe when done
subscription.Unsubscribe();
```

To drop a subscription for good, call `subscription.Dispose()` or `client.RemoveSubscription(subscription)`. The
client forgets it immediately, so the channel can be subscribed again with a new subscription. It is then
unsubscribed the same way `Unsubscribe()` does it: an unsubscribe command goes to the server if the server may hold
the subscription, followed by `StateChanged` and `Unsubscribed` (a handler of these may already create the new
subscription). Once disposed, a subscription can't be subscribed again.

### Token Authentication

```csharp
var options = new CentrifugeClientOptions
{
    // Provide initial token
    Token = "your-jwt-token",

    // Optional: provide token refresh callback
    GetToken = async () =>
    {
        // Fetch new token from your backend
        var response = await httpClient.GetAsync("https://your-backend.com/centrifugo/token");
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return tokenResponse.Token;
    }
};

var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
```

### Channel-Specific Token

```csharp
var subscriptionOptions = new CentrifugeSubscriptionOptions
{
    Token = "channel-specific-jwt",

    GetToken = async (channel) =>
    {
        // Fetch channel-specific token from your backend
        var response = await httpClient.PostAsJsonAsync(
            "https://your-backend.com/centrifugo/subscription_token",
            new { channel }
        );
        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return tokenResponse.Token;
    }
};

var subscription = client.NewSubscription("private-channel", subscriptionOptions);
subscription.Subscribe();
```

### History API

```csharp
// Get last 10 messages
var history = await subscription.HistoryAsync(new CentrifugeHistoryOptions
{
    Limit = 10
});

foreach (var pub in history.Publications)
{
    var data = Encoding.UTF8.GetString(pub.Data.Span);
    Console.WriteLine($"Message: {data}");
}
```

### Presence API

```csharp
// Get all clients in channel
var presence = await subscription.PresenceAsync();
foreach (var client in presence.Clients.Values)
{
    Console.WriteLine($"Client: {client.Client}, User: {client.User}");
}

// Get presence stats (counts only)
var stats = await subscription.PresenceStatsAsync();
Console.WriteLine($"Clients: {stats.NumClients}, Users: {stats.NumUsers}");
```

### RPC Calls

```csharp
var request = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
var result = await client.RpcAsync("my_method", request);
var response = Encoding.UTF8.GetString(result.Data.Span);
Console.WriteLine($"RPC result: {response}");
```

### Multi-Transport Fallback

For environments where WebSocket might be blocked, use automatic transport fallback:

```csharp
using Centrifugal.Centrifuge;

// Define transport endpoints to try in order
var transports = new[]
{
    new CentrifugeTransportEndpoint(
        CentrifugeTransportType.WebSocket,
        "ws://localhost:8000/connection/websocket"
    ),
    new CentrifugeTransportEndpoint(
        CentrifugeTransportType.HttpStream,
        "http://localhost:8000/connection/http_stream"
    )
};

// Create client with transport fallback
var client = new CentrifugeClient(transports);

// Client will try WebSocket first, then fall back to HTTP Stream if needed
client.Connect();
```

**Note**: In Blazor WebAssembly, ensure you've called `builder.Services.AddCentrifugeClient()` first (see Blazor Support section). The SDK will automatically use browser-native transports.

### Advanced Configuration

```csharp
var options = new CentrifugeClientOptions
{
    // Connection token and refresh
    Token = "your-jwt-token",
    GetToken = async () => await FetchTokenAsync(),

    // Connection data
    Data = Encoding.UTF8.GetBytes("{\"custom\":\"data\"}"),

    // Client identification (used for observability)
    Name = "my-app",
    Version = "1.0.0",

    // Reconnection settings
    MinReconnectDelay = TimeSpan.FromMilliseconds(500),
    MaxReconnectDelay = TimeSpan.FromSeconds(20),

    // Timeouts: Timeout bounds a transport open, the wait for a connection (calls made while
    // connecting) and then a command's reply, from when it is sent (as centrifuge-js)
    Timeout = TimeSpan.FromSeconds(5),
    MaxServerPingDelay = TimeSpan.FromSeconds(10),

    // Logging (pass ILogger for debug output)
    Logger = loggerFactory.CreateLogger<CentrifugeClient>(),

    // Custom headers (works over header emulation, requires Centrifugo v6+)
    Headers = new Dictionary<string, string>
    {
        ["X-Custom-Header"] = "value"
    }
};

var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
```

Creating a client or subscription with invalid options throws `CentrifugeConfigurationException`. The checks:

- no delay is negative, and no maximum delay is smaller than its minimum;
- `Timeout` is positive;
- `Timeout`, `MaxReconnectDelay` and the subscription `MaxResubscribeDelay` fit in `Int32.MaxValue` milliseconds;
- for an HTTP streaming endpoint, `EmulationEndpoint` is an absolute http/https URL (in the browser it, like the
  fallback endpoints, may also be a page-relative URL).

### Recovery and Positioning

```csharp
var options = new CentrifugeSubscriptionOptions
{
    // Enable recovery (if supported by server)
    Recoverable = true,

    // Enable positioning
    Positioned = true,

    // Start from known position
    Since = new CentrifugeStreamPosition(offset: 100, epoch: "epoch-id")
};

var subscription = client.NewSubscription("chat", options);

subscription.Subscribed += (sender, e) =>
{
    if (e.WasRecovering)
    {
        Console.WriteLine($"Recovery attempted, recovered: {e.Recovered}");
    }
};

subscription.Subscribe();
```

### Server-Side Publication Filtering

Filter publications on the server side based on publication tags, so unwanted messages
never reach the client. Build filter expressions with `CentrifugeFilterNodeBuilder`
and pass them via `TagsFilter` (or `SetTagsFilter` to change the filter later). Cannot
be used together with delta compression.

```csharp
var options = new CentrifugeSubscriptionOptions
{
    // Only deliver publications tagged with ticker=BTC and price > 50000
    TagsFilter = CentrifugeFilterNodeBuilder.And(
        CentrifugeFilterNodeBuilder.Eq("ticker", "BTC"),
        CentrifugeFilterNodeBuilder.Gt("price", "50000")
    )
};

var subscription = client.NewSubscription("prices", options);
subscription.Subscribe();

// Change the filter later - applied on the next (re)subscription attempt
subscription.SetTagsFilter(CentrifugeFilterNodeBuilder.In("ticker", "BTC", "ETH", "SOL"));
```

Available comparisons: `Eq`, `Neq`, `In`, `Nin`, `Ex`, `Nex`, `StartsWith`, `EndsWith`,
`Contains`, `Lt`, `Lte`, `Gt`, `Gte`, combined with `And`, `Or`, `Not`.

## What the fork adds

The sections below describe the features of the ImpulseM fork. They are not in upstream centrifuge-csharp. An application that does not set an option keeps the upstream behaviour.

### RPC options and headers

Headers can be set for one rpc call. They travel with that call only:

```csharp
var options = new CentrifugeRpcOptions().WithHeader("x-request-source", "mobile");
var result = await client.RpcAsync("my.method", data, options);
```

Headers need a server that confirms RPC options. The client advertises the capability in the connect request, and the server confirms it in the connect reply. Check the confirmed capability on the client:

```csharp
if (client.Capabilities.RpcOptionsSupported)
{
    // per-call headers can be sent
}
```

The capability belongs to the current connection. It is `false` until a connect reply confirms it, and it is cleared when the transport closes. When it is `false`, a call with headers fails at once with `CentrifugeErrorCodes.Unsupported` (15), and the call is not sent. A call with no options, or with empty ones, is a plain call and works either way.

Errors from the server can carry application fields. They are empty or zero when the server did not send them:

* `CentrifugeException.AppCode`: the numeric application error code, `0` when not set.
* `CentrifugeException.AppType`: the application error type, empty when not set.
* `CentrifugeException.Metadata`: a read-only copy of the allow-listed server metadata, empty when not set.

`client.ServerTimeMillis()` returns the current time in milliseconds on the server clock. It is estimated from the server time of the last reply that carried one, and falls back to the local time until a reply has carried a server time.

### Cancellable calls

`RpcCall` returns a `CentrifugeRpcCall` handle. Its `Result` task completes with the outcome of the call, and `Cancel()` cancels it:

```csharp
var call = client.RpcCall("my.method", data);
call.Cancel();
```

A cancelled call is rejected with `CentrifugeErrorCodes.Cancelled` (17). A call which was not sent yet is never sent. When the call was sent, `Cancel()` asks the server to stop it too, over the current connection or the next one that confirms RPC options. Without that capability nothing is cancelled on the server, and a call that was sent is rejected with `Unsupported`. A null method throws `ArgumentNullException` at the call.

### Exactly-once calls and the connection instance

With a server that confirms RPC options and an instance set in `CentrifugeClientOptions.Instance`, every rpc call carries a `msg_id`, and the server runs each `msg_id` once. A call that is lost with its transport before its reply is kept, and is sent again after the next connect with the same `msg_id` and the retransmit flag set. The server answers from its record, so the call does not run twice. The result then has `Cached` set to `true`.

Without an instance a call is plain: it carries no `msg_id`, is not kept across a lost transport, and fails with a `CentrifugeException` with code 10 or 11 when the transport is lost. A call keeps its original deadline across resends. A call whose deadline passes fails with `CentrifugeTimeoutException`, and a call which was sent is also cancelled on the server.

The server can refuse a call that it did not run. The refusal carries the metadata key `x-centrifugo-retry`:

* `new_msg_id`: the `msg_id` was outside the server window. The client re-syncs its clock to the server time of that reply and sends the call again with a new `msg_id`, at most twice.
* `later`: the call did not run and its `msg_id` is still good. The client sends the same `msg_id` again after a random delay (full jitter) of up to 250 ms, 500 ms and 1 s, at most three times.

The deadline of the call bounds all attempts. A refused call has not run, so a resend never runs it twice.

`CentrifugeClientOptions.MaxPendingRpc` (32 by default) bounds the unsettled calls of a client with an instance. A call beyond the bound fails at once with `CentrifugeErrorCodes.LimitExceeded` (16) and is not sent.

The instance is a secret that the application supplies. The SDK never generates it and never stores it. The application must:

* generate a random secret of at least 128 bits: 22 to 64 characters from `A-Za-z0-9_-`;
* keep it per installation or per tab, in storage of its own;
* never log or share it. Whoever knows it can retransmit and cancel calls in its scope.

```csharp
var instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
// keep the value in the application's own storage, then:
var options = new CentrifugeClientOptions { Instance = instance };
```

An instance outside that pattern throws `ArgumentException`, and the message does not contain the value. The instance is read when the client is created, so a later change of the options does not change that client. Without an instance a call has no scope on the server, so it is not deduplicated.

### RPC pool for large payloads

`CentrifugeRpcPool` sends rpc calls with large payloads over several plain connections per endpoint, with a window of calls per connection, a bounded wait queue and resends. The application names the endpoints through a resolver, keyed by an endpoint key that it chooses (for example a region), and gives each call an optional affinity hint, which the server issued and sends in the header named by `AffinityHeader`:

```csharp
var pool = new CentrifugeRpcPool(
    endpointKey => new[] { "wss://blobs.example/connection/websocket" },
    new CentrifugeRpcPoolOptions
    {
        TokenFrom = client,
        AffinityHeader = "x-example-affinity",
        MaxConnectionsPerEndpoint = 2,
        MaxInflightPerConnection = 1
    });

var result = await pool.RpcAsync(
    "blob.writePart",
    chunk,
    new CentrifugeRpcCallOptions { EndpointKey = "eu-2", Affinity = hint });
```

* The calls are plain: no `msg_id` and no instance. A resent call may run again on the server, so the method must be idempotent.
* `MaxInflightPerConnection` is the window, 1 by default. The server must allow it: its `client.concurrency` setting must be at least the window plus one.
* A call is sent again, up to `Retries` times (3 by default), when its connection was lost, when the server refused it with `x-centrifugo-retry: later`, or when `Reroute` maps the server error to another endpoint key. A timeout is never resent. A connection that the server closed for a frame over its size limit (WebSocket 1009) settles its calls without a resend.
* `Redirect` lets the application move the bytes of a reply to a URL, for example a presigned one. The pool fetches it within the call's deadline, at most `MaxRedirectsInflight` at a time, and the body is bounded by `CentrifugeRpcPoolRedirect.MaxBytes` (16 MiB by default). The pool follows no redirect of the fetch: a 3xx answer fails it with `CentrifugeErrorCodes.RedirectFailed` (18). A URL that the endpoint does not allow fails the same way; an `https` URL is needed for a `wss` endpoint. On a plaintext endpoint (`ws://`) the redirect body and headers travel unprotected, so use `wss://` for anything secret.
* `MaxQueued` (64 by default) bounds the calls that wait for a connection. A call beyond it fails at once with `LimitExceeded`. The calls waiting for a redirect fetch slot count in it too. `IdleTimeout` (60 seconds by default; zero keeps the connections) closes a connection with no call. `GetStatsAsync()` reports the connections, the calls in flight and waiting, and the redirect fetches.
* `TokenFrom = client` takes the token from the main client. A pool connection takes only the token that the server confirmed at the main client's last connect or accepted refresh, never a token the application set and the server has not confirmed. When a pool connection is refused its token, the main client renews itself once for it. `TokenFrom` is exclusive with `Token` and `GetToken`. While the main client connects, a call waits for it. While the main client is disconnected for good, a call fails at once with an `IOException`.
* `Close()` closes every connection at once. The calls that wait for a connection fail with `CentrifugeErrorCodes.ClientClosed` (4), and the calls on the wire and the redirect fetches fail with an `IOException`.

### Refreshing the connection token on demand

`RefreshAsync` replaces the connection token now, without waiting for the server to ask for one:

```csharp
var auth = await client.RefreshAsync(newToken);
Console.WriteLine($"User: {auth.User}, auth class: {auth.AuthClass}");
```

On success the call returns the identity that the server reports, and the refresh timer is rescheduled from the new TTL. The token of the connection is replaced only by a token that the server accepted. An error is returned to the caller unchanged, with its code and metadata. The connection, its identity and its refresh timer stay as they were, and the refresh is not retried: the caller asked for it, so the caller decides. A `cancellationToken` only detaches the caller; the refresh still runs.

Rpc calls issued while a refresh is in progress wait for its reply. A refresh with the token of a pending refresh shares its command. A refresh made while the client is not connected sets the token of the next connect, and the previous token is restored when the server refuses that refresh. At most 32 refreshes are in progress; a refresh beyond that fails at once with `LimitExceeded`. A refresh with a null token throws `ArgumentNullException` before anything is queued.

The identity of the connection is reported through the `Auth` event, on every connect and whenever a refresh changes the user or the auth class:

```csharp
client.Auth += (sender, e) =>
{
    Console.WriteLine($"User: {e.User}, auth class: {e.AuthClass}");
};
```

The refresh timer runs before the token expires. Its errors follow the upstream rule, with two deviations:

| Error of the timer refresh | Behaviour |
|---|---|
| temporary, or a client error (code below 100) | reported through `Error`, retried with backoff and a fresh token (upstream) |
| 109 token expired | reported through `Error`, retried with backoff and a fresh token. **Deviation:** upstream disconnects with 109 on a refresh. |
| 103 permission denied | reported through `Error`, the connection and its identity are kept, and there is no retry. **Deviation:** upstream disconnects with 103. The server ends the connection at its expiry, and the reconnect fetches a token from `GetToken`, so the identity changes then. An application that needs the change now calls `RefreshAsync` and handles 103. |
| anything else | the connection is disconnected with the server's code, and does not reconnect (upstream) |

A failed timer fetch or refresh waits 10 to 20 seconds before the next try. Each further failure in a row doubles the wait, up to `CentrifugeClientOptions.TokenRetryCap`, which is 5 minutes by default. A success starts the delay again. Set the cap to 20 seconds or less to keep the flat retry.

### Token exchange over the connection

Instead of a connection token, the application can hand the client a long-lived refresh token once, and let the client obtain and renew the short access token itself, over the same socket. The application persists the refresh token and supplies three operations by deriving from `CentrifugeTokenExchange`: `Load()` reads it, `Store()` persists a rotated one, and `ExchangeAsync()` trades it for an access token. `CentrifugeClientOptions.TokenExchange` turns the feature on. It is exclusive with `GetToken`: a client created with both throws `CentrifugeConfigurationException`.

```csharp
public sealed class FileTokenExchange : CentrifugeTokenExchange
{
    private readonly string path;

    public FileTokenExchange(string path)
    {
        this.path = path;
    }

    public override string? Load()
    {
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public override void Store(string refreshToken)
    {
        File.WriteAllText(path, refreshToken);
    }

    public override async Task<CentrifugeTokenExchangeResult> ExchangeAsync(
        CentrifugeTokenExchangeEvent exchangeEvent,
        CancellationToken cancellationToken)
    {
        // Encode the request with your own codec. The rpc goes over the connection the exchange runs on.
        var reply = await exchangeEvent.RpcAsync("auth.exchange", EncodeRequest(exchangeEvent.RefreshToken), cancellationToken);

        // Decode the reply with your own codec: the access token, the rotated refresh token if there is one,
        // and the grace in seconds that the issuer keeps the consumed refresh token for.
        return DecodeReply(reply.Data);
    }

    // The app type that the backend sets on a refused refresh token.
    public override IReadOnlyCollection<string> RefusalTypes => new[] { "REFRESH_TOKEN_REFUSED" };

    // Used until the issuer publishes one in a result. The default is 30 seconds.
    public override int GraceSeconds => 30;
}

var options = new CentrifugeClientOptions { TokenExchange = new FileTokenExchange(tokenPath) };
var client = new CentrifugeClient("ws://localhost:8000/connection/websocket", options);
client.Connect();
```

`EncodeRequest` and `DecodeReply` stand for the application's own codec.

The backend answers a refused refresh token with an error whose app type is listed in `RefusalTypes`. The client classifies a refusal by that name, and never by the error code or the HTTP status. An rpc rejected with a listed name fails with `CentrifugeTokenExchangeRefusedException`, which carries the server error as `ServerError`. An rpc rejected with any other name is an ordinary error, and the exchange is retried with backoff. The list is empty by default.

The flow: when the connect reply is anonymous (it has no user), the client runs the exchange on the new socket before it reports the connection as connected. It then sets the access token as the connection token, which binds the connection to the user. Every rotated refresh token goes to `Store()` before the access token issued with it is used. The client renews the access token over the connected socket at a random moment between 70% and 90% of its TTL (60% to 90% when `EarlyRenewal` is `true`), so a fleet which connected together spreads its renewals. Without a token exchange the refresh timer runs at the full TTL.

Several tabs or processes of one installation can share one refresh token. The client reads `Load()` immediately before each exchange and stores the rotated token immediately after. The backend keeps a grace for the previous refresh token, so two clients which exchange the same token inside the grace both get the same rotated refresh token, and each its own access token.

A refused refresh token is not the end of the session at once. When the issuer refuses the token the client presented, a refusal run starts. The client reads `Load()` again and presents the token it finds at once when it changed, and presents the same token again once otherwise, at a random moment inside the remaining grace. A second refusal of a token that did not change ends the session, and so does a refusal at the horizon. The horizon is the grace, measured from the first refusal of the run. An accepted result can publish a new grace; a value above 300 seconds is clamped to 300. The session is dead at the second refusal of an unchanged token, at a refusal at the horizon, or when `Load()` returns nothing. The client then does not reconnect and raises `Disconnected` with code 1 (`unauthorized`). Within the run a refusal is reported through `Error`, and the message names no token.

Failures of the exchange are reported through `Error`: `connectToken` (code 5) on bootstrap, and `refreshToken` (code 6) on renewal. A `Load()` that throws `CentrifugeUnauthorizedException` ends the session at bootstrap and at renewal. Any other exception is reported and retried with backoff.

### Publish debouncing and channel compaction

When the server returns a publish debounce window in the subscribe result, `Subscription.PublishAsync` coalesces rapid publishes. The first publish is sent at once and starts a window of that length. Publishes inside the window replace the pending value and complete at once. When the window ends with a pending value, the latest one is sent and a new window starts. The window is off unless the server sets it, and it has no maximum except the 32-bit limit of its length.

Debouncing is fire-and-forget: pending data is dropped on `Unsubscribe()` and on disconnect, and an error of a debounced send that goes out when the window ends is not reported to the application.

The client always offers channel compaction on subscribe. When the server allows it, publication, join and leave pushes carry a numeric channel id instead of the channel name. It is negotiated automatically and needs no option.

A subscription token refresh that the server refuses with 109 is retried with the connection's backoff and a fresh token, and the refused token is not sent again while `StopOnRefusedToken` is `true` (the default). A refusal with 103 keeps the subscription, arms no timer and is reported through `Error`. Other codes unsubscribe the subscription with the server code.

## Error Handling

```csharp
try
{
    await client.RpcAsync("method", data);
}
catch (CentrifugeUnauthorizedException)
{
    Console.WriteLine("Authentication failed");
}
catch (CentrifugeTimeoutException)
{
    Console.WriteLine("Operation timed out");
}
catch (CentrifugeException ex)
{
    Console.WriteLine($"Centrifuge error: {ex.Code} - {ex.Message}");
    Console.WriteLine($"Temporary: {ex.Temporary}");
}
```

A `CentrifugeTimeoutException` comes either from waiting for the client or subscription to become ready
(`ReadyAsync`, which the calls also await) or from a reply that didn't come within `Timeout`. In the second case the
command may or may not have reached the server. Other failures use `CentrifugeException` codes: `TransportWriteError`
when the command couldn't be written, `ConnectionClosed` when the connection went away before the reply.

## Working with JSON Data

The SDK uses binary data (`ReadOnlyMemory<byte>`) for all payloads, but this doesn't mean you can't use JSON. JSON is simply a UTF-8 encoded byte sequence, so it works seamlessly:

```csharp
// Sending JSON
var message = new { text = "Hello", timestamp = DateTime.UtcNow };
var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message);
await subscription.PublishAsync(jsonBytes);

// Receiving JSON
subscription.Publication += (sender, e) =>
{
    // Option 1: Deserialize directly from bytes (zero-copy, recommended)
    var message = JsonSerializer.Deserialize<MyMessage>(e.Data.Span);

    // Option 2: Convert to string first
    var json = Encoding.UTF8.GetString(e.Data.Span);
    Console.WriteLine($"Received: {json}");
};
```

Using `ReadOnlyMemory<byte>` instead of `string` provides flexibility - you can work with JSON, MessagePack, Protobuf, or any other serialization format. The `.Span` property enables zero-copy parsing with modern serializers like `System.Text.Json`.

## Design Notes

### Non-Async Connect and Subscribe Methods

You may notice that `Connect()` and `Subscribe()` methods are not async and don't return a `Task`. This is an intentional design choice:

```csharp
client.Connect();           // Returns void, not Task
subscription.Subscribe();   // Returns void, not Task
```

**Why?**

1. **Lifecycle Management**: The SDK manages connection and subscription lifecycle internally, including automatic reconnection and resubscription. Calling `Connect()` expresses intent ("I want to be connected"), and the SDK maintains that intent across reconnects.

2. **Automatic Batching**: This design enables automatic batching of subscription requests. When you call `Subscribe()` on multiple subscriptions before or right after `Connect()`, the SDK batches them into a single protocol message:

```csharp
var sub1 = client.NewSubscription("channel1");
var sub2 = client.NewSubscription("channel2");
var sub3 = client.NewSubscription("channel3");

sub1.Subscribe();
sub2.Subscribe();
sub3.Subscribe();

client.Connect();
// All 3 subscriptions are batched together in one request!
```

3. **Consistency**: This pattern is consistent across all Centrifuge client SDKs (JavaScript, Go, Swift, Dart, etc.).

**If you need to wait for connection/subscription:**

```csharp
client.Connect();
await client.ReadyAsync();  // Wait until connected

subscription.Subscribe();
await subscription.ReadyAsync();  // Wait until subscribed
```

## Command Batching

The SDK automatically batches commands where possible for improved network efficiency. This is especially beneficial for HTTP-based transports.

- Commands are automatically batched with a 1ms delay window
- Batches are flushed immediately when they exceed 15KB to prevent oversized requests
- No configuration needed - batching is automatic

When you issue multiple commands without awaiting them immediately, they are queued and sent together in a single batch:

```csharp
// ❌ Sequential (no batching)
await subscription.PublishAsync(data1);
await subscription.PublishAsync(data2);
await subscription.PresenceAsync();
// Result: 3 separate network requests

// ✅ Batched (recommended)
var task1 = subscription.PublishAsync(data1);
var task2 = subscription.PublishAsync(data2);
var task3 = subscription.PresenceAsync();

await Task.WhenAll(task1, task2, task3);
// Result: 1 network request with all 3 commands!
```

Also, SDK automatically batches subscription requests under the hood (where possible) including re-subscriptions upon reconnect.

See the [Blazor example](examples/Centrifugal.Centrifuge.BlazorExample/Pages/Home.razor) for a complete demonstration of command batching in action.

## Unity Support

The library works with Unity 2021.2+ (requires .NET Standard 2.1 support).

**Platform Support:**
- ✅ **Standalone builds** (Windows, macOS, Linux): Works out of the box
- ✅ **Mobile** (iOS, Android): Works out of the box
- ⚠️ **WebGL**: Requires a WebSocket plugin that provides `System.Net.WebSockets` support, such as:
  - [NativeWebSocket](https://github.com/endel/NativeWebSocket)
  - [WebSocketSharp](https://github.com/sta/websocket-sharp) with Unity WebGL wrapper

Example for Unity (standalone/mobile):

```csharp
using UnityEngine;
using Centrifugal.Centrifuge;
using System.Threading.Tasks;

public class CentrifugeManager : MonoBehaviour
{
    private CentrifugeClient client;

    async void Start()
    {
        client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

        client.Connected += (sender, e) =>
        {
            Debug.Log($"Connected: {e.ClientId}");
        };

        client.Connect();
    }

    void OnDestroy()
    {
        if (client != null)
        {
            // Dispose() blocks until disconnect completes
            client.Dispose();
        }
    }
}
```

> **Note**: Unity WebGL builds run in the browser but don't have built-in `System.Net.WebSockets` support. You'll need a third-party WebSocket library that works in Unity WebGL.

## Blazor Support

The library works in both Blazor Server and Blazor WebAssembly.

### Blazor Server

Works out of the box - uses standard .NET WebSocket client. No special configuration needed.

### Blazor WebAssembly

The SDK provides a simple DI-based configuration approach, similar to SignalR. Register Centrifuge services once in `Program.cs`:

```csharp
using Centrifugal.Centrifuge;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add Centrifuge client services - automatically initializes browser interop
builder.Services.AddCentrifugeClient();

await builder.Build().RunAsync();
```

Then create and use clients anywhere in your app:

```csharp
@page "/"
@inject IJSRuntime JS
@inject ILoggerFactory LoggerFactory
@implements IAsyncDisposable
@using System.Text

<h3>Messages</h3>
<ul>
    @foreach (var msg in messages)
    {
        <li>@msg</li>
    }
</ul>

@code {
    private CentrifugeClient? _client;
    private List<string> messages = new();

    protected override async Task OnInitializedAsync()
    {
        // Create client directly - no need to pass IJSRuntime
        _client = new CentrifugeClient(
            "ws://localhost:8000/connection/websocket",
            options: new CentrifugeClientOptions
            {
                Logger = LoggerFactory.CreateLogger<CentrifugeClient>()
            }
        );

        _client.Connected += (sender, e) =>
        {
            messages.Add($"Connected: {e.ClientId}");
            InvokeAsync(StateHasChanged);
        };

        var subscription = _client.NewSubscription("chat");

        subscription.Publication += (sender, e) =>
        {
            var data = Encoding.UTF8.GetString(e.Data.Span);
            messages.Add($"Received: {data}");
            InvokeAsync(StateHasChanged);
        };

        _client.Connect();
        subscription.Subscribe();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}
```

**How it works:**

- The SDK automatically uses browser-native transports when `AddCentrifugeClient()` is called
- WebSocket uses browser's native WebSocket via JS interop
- HTTP Stream uses browser's Fetch API with ReadableStream
- No need to pass `IJSRuntime` to constructors - it's configured globally

**Important**: The JavaScript interop modules are automatically included as static assets. Ensure your `index.html` has the standard Blazor script tag:

```html
<script src="_framework/blazor.webassembly.js"></script>
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/Impulse-m/ImpulseM.Centrifugo.Client.CSharp.git
cd ImpulseM.Centrifugo.Client.CSharp

# Build the project
dotnet build

# Run tests
dotnet test

# Pack NuGet package
dotnet pack -c Release
```

## Publishing to NuGet

The package `ImpulseM.Centrifuge` is published to nuget.org by the `release` GitHub Actions workflow, through trusted publishing: no API key is stored in the repository. Each release is a new, immutable version, with its symbol package, the licence and the third-party notices.

To release a version, run the `release` workflow with the version (for example `1.0.0`), or push the tag `v1.0.0` on a commit of `main`. The workflow runs the tests, packs the library, pushes it to nuget.org, waits until nuget.org serves the version, creates the tag `v1.0.0` (dispatch only; a tag push already has it) and then creates the GitHub Release. The project file is not edited for a release.

See [PUBLISHING.md](PUBLISHING.md) for the one-time setup and the details of each job.

## Architecture

The SDK follows the [Centrifuge protocol specification](https://centrifugal.dev/docs/transports/protocol) and implements:

- **Client State Machine**: Manages connection lifecycle (Disconnected → Connecting → Connected)
- **Subscription State Machine**: Manages subscription lifecycle (Unsubscribed → Subscribing → Subscribed)
- **Transport Layer**: Abstraction over WebSocket and HTTP-streaming
- **Exponential Backoff**: Smart reconnection with full jitter to avoid thundering herd
- **Varint Encoding**: Efficient Protobuf message framing
- **Event Loop**: every state change runs as a step of one loop per client, following [centrifuge-js](https://github.com/centrifugal/centrifuge-js) and the SDK specification; events are raised in the order of the steps

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Submit a pull request

## License

MIT License - see [LICENSE](LICENSE) file for details.

The NuGet package carries the `LICENSE` file and the [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) file at its root. The notices cover the Fossil delta decoder, which is a port under the BSD 2-Clause license.

## Links

- [Centrifugo Documentation](https://centrifugal.dev/)
- [Client SDK Specification](https://centrifugal.dev/docs/transports/client_api)
- [Centrifuge Protocol](https://centrifugal.dev/docs/transports/protocol)
- [GitHub Repository](https://github.com/Impulse-m/ImpulseM.Centrifugo.Client.CSharp)
- [NuGet Package](https://www.nuget.org/packages/ImpulseM.Centrifuge/)

## Support

- GitHub Issues: [Report bugs or request features](https://github.com/Impulse-m/ImpulseM.Centrifugo.Client.CSharp/issues)
- [Community rooms](https://centrifugal.dev/docs/getting-started/community)
