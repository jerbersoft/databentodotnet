# Live Streaming

**A live session is five calls in a fixed order, and the fifth one starts the bill.** This page
covers the whole lifecycle: connecting, the CRAM handshake, subscriptions, the two record loops,
reconnection, and the timeouts that decide when a quiet feed becomes an error.

For a first working program, start with [Getting Started](getting-started.md). This page assumes you
have one.

---

## The session lifecycle

```
ConnectAsync  →  AuthenticateAsync  →  SubscribeAsync  →  StartAsync  →  records
                                            ↑                 │
                                            └── free ─────────┴── billed ──→
```

`LiveClient` splits connection from authentication, where upstream's `Client::build()` does both
at once. That is what makes `ConnectTimeoutException` and `AuthTimeoutException` nameable as the
different failures they are — a gateway you cannot reach and a key it will not accept are not the
same problem.

**Nothing before `StartAsync` moves market data.** A subscription tells the gateway what to send
*later*; the gateway sends nothing at all until the session is started. That is why the
repository's own smoke tests against the real gateway are free and its one session test sits
behind a second environment gate.

```csharp
using DatabentoDotNet;          // ApiKey, Symbols
using DatabentoDotNet.Dbn;      // Metadata, Schema, SType, the record structs
using DatabentoDotNet.Live;     // LiveClient, Subscription

await using var client = new LiveClient
{
    ApiKey  = new ApiKey(apiKeyString),
    Dataset = "EQUS.MINI",
};

await client.ConnectAsync(ct);
await client.AuthenticateAsync(ct);
await client.SubscribeAsync(new Subscription
{
    Schema  = Schema.Trades,
    Symbols = Symbols.From("AAPL"),
}, ct);

Metadata metadata = await client.StartAsync(ct);
```

`await using` matters: `DisposeAsync` closes the socket. A client that goes out of scope without
it leaves a connection open on the gateway's side until it times out.

## Constructing the client

`ApiKey` and `Dataset` are `required`; everything else has a default. There is no builder —
upstream's generic type-state `ClientBuilder<AK, D>` exists to make "no API key" unrepresentable,
and C# `required` init properties do that natively, checked by the compiler at every construction
site.

| Property | Default | Notes |
|---|---|---|
| `ApiKey` | *required* | Validated at construction. Renders as `…a1b2c` in logs and exceptions |
| `Dataset` | *required* | e.g. `EQUS.MINI`, `XNAS.ITCH`, `GLBX.MDP3`. Also picks the gateway host |
| `Compression` | `None` | `Zstd` compresses the record stream. Settled on the auth line, not later |
| `SendTsOut` | `false` | Ask the gateway to append its send timestamp to each record |
| `HeartbeatInterval` | `null` | 5–1800 seconds, whole seconds only. `null` leaves it to the gateway |
| `SlowReaderBehavior` | `null` | `Warn` or `Skip`. `null` leaves it to the gateway |
| `UpgradePolicy` | `UpgradeToV3` | How records from older DBN versions are handled |
| `ConnectTimeout` | 10 s | Budget for `ConnectAsync` |
| `AuthTimeout` | 10 s | Budget for the whole handshake, not per line |
| `ReadTimeout` | `null` | See [Timeouts](#timeouts-and-heartbeats) below |
| `Gateway` | `null` | Override the resolved address. For tests against a mock |

`HeartbeatInterval` is validated **client-side**, which upstream does not do. A value outside
5–1800 seconds raises `ArgumentOutOfRangeException` immediately rather than costing a round trip
and a closed connection to discover, and a fractional-second value raises `ArgumentException`
rather than being silently truncated — an interval that differs between your code and the wire is
exactly the kind of confidently-wrong behaviour this library is built to avoid.

### Gateway resolution

`LiveGateway.For(dataset)` derives the host from the dataset: `GLBX.MDP3` becomes
`glbx-mdp3.lsg.databento.com:13000`. `ConnectAsync` does this for you. Set `Gateway` explicitly
only when you are pointing at a mock, or at an address you have specifically been told to use.

## Authentication

`AuthenticateAsync` runs the CRAM handshake: the gateway sends a challenge, the client answers
with a SHA-256 of the challenge and the key, and the gateway replies `success=1|session_id=…`.
Afterwards `Greeting` holds the gateway's version banner verbatim and `SessionId` holds the id —
both are diagnostics, and `Greeting` is deliberately not parsed.

**The handshake is not cancel-safe, and the client does not pretend otherwise.** A half-written
authentication line desynchronises the gateway, which then closes the connection. Cancelling
`AuthenticateAsync` therefore tears the socket down rather than abandoning a partial write. A
caller whose `AuthenticateAsync` throws — for any reason, cancellation included — is disconnected
and must connect again. There is no resuming it.

The same applies to `SubscribeAsync`, `ResubscribeAsync`, and `StartAsync`. All four write control
lines, and half a control line is not recoverable.

## Subscriptions

```csharp
var sent = await client.SubscribeAsync(new Subscription
{
    Schema   = Schema.Mbp1,
    Symbols  = Symbols.From(["ES.FUT", "NQ.FUT"]),
    StypeIn  = SType.Parent,
}, ct);

Console.WriteLine(sent.Id);      // the correlation id the gateway will quote in any error
```

| Property | Default | Notes |
|---|---|---|
| `Schema` | *required* | `Trades`, `Mbo`, `Mbp1`, `Mbp10`, `Ohlcv1S`…`Ohlcv1D`, `Definition`, `Status`, … |
| `Symbols` | *required* | `Symbols.From(...)`, `Symbols.FromIds(...)`, or `Symbols.All` |
| `StypeIn` | `RawSymbol` | How to interpret the symbols — `Parent`, `Continuous`, `InstrumentId`, … |
| `Start` | `null` | Intraday replay: begin from this `Instant` rather than from now |
| `UseSnapshot` | `false` | Send the current book state first. **MBO only**, and not with `Start` |
| `Id` | assigned | A correlation handle. Leave it unset and the client assigns a monotonic one |

`SubscribeAsync` **returns what it sent**, with `Id` filled in, rather than mutating your object —
`Subscription` is an immutable record. The sent form is also appended to `client.Subscriptions`,
which is the list `ResubscribeAsync` replays.

### Symbol counts and chunking

The gateway caps a subscription line at 500 symbols. `SubscribeAsync` splits automatically, one
line per chunk, with `is_last=1` on the final line only — that flag is what makes a chunked
subscription *one* subscription rather than several partial ones. You do not need to chunk
yourself; `Symbols.ChunkCount` tells you how many lines a given set will take.

`Symbols.All` subscribes to everything the dataset carries. On a busy dataset that is a very large
amount of data, and the bill reflects it.

### Validation happens before the socket

A subscription that combines `UseSnapshot` with `Start`, asks for a snapshot on a schema other
than MBO, or names no symbols raises `ArgumentException` and **writes nothing**. That check runs
before the connection check too, so a subscription the client would never send is rejected the
same way whether or not a socket happens to be open.

### Subscribing after the session starts

Legal, and the same code path. The gateway distinguishes a mid-session subscription from a
pre-session one; this client does not need to.

## Reading records

Two surfaces, and the difference is whether records may cross an `await`.

### The zero-copy loop

```csharp
while (true)
{
    while (client.TryNextRecord(out RecordRef record))
    {
        if (record.TryGet(out TradeMsg trade))
        {
            Process(trade);          // `record` dies here — do not store it
        }
    }

    if (await client.FillBufferAsync(ct) == 0)
    {
        break;                       // the gateway closed the stream cleanly
    }
}
```

**This allocates nothing per record.** `TryNextRecord` reinterprets the record in place over the
read buffer, and `FillBufferAsync` fills that same buffer. The repository asserts this: a test
measures allocated bytes around a steady-state loop over the mock gateway's socket and requires
exactly zero.

The split into two calls is not an API wart. An `async` method cannot return a `ref struct`, so
there is no `Task<RecordRef>` and there never can be — upstream's single-call `next_record()` does
not port, and its `fill_buf()` / `try_next_record()` pair does. The compiler enforces the same
lifetime rule the loop above follows by hand: a `RecordRef` that survives an `await` is a
compile error (CS4007), not a runtime surprise.

`FillBufferAsync` returns `0` exactly once the stream ends, and every later call returns `0`
without touching the socket. `client.IsClosed` distinguishes "no record yet" from "no record
ever" — `TryNextRecord` returns `false` for both.

### The convenient loop

```csharp
await foreach (var record in client.RecordsAsync(ct))
{
    if (record.TryGet(out TradeMsg trade))
    {
        Process(trade);
    }
}
```

`RecordsAsync` yields `OwnedRecord`, a heap copy. `yield return` carries the same restriction
`await` does, so a `ref struct` cannot leave an iterator at all — the copy is necessary, not a
convenience shortcut that could be optimised away. It costs **two allocations per record**, which
is roughly 110 bytes for a typical trade.

Use it when records are individually interesting and the rate is modest. Use the zero-copy loop
for full-depth MBO at market open. `RecordsAsync` is written in terms of `FillBufferAsync` and
`TryNextRecord` and does not bypass them, so the two surfaces cannot diverge.

More on the trade-off, and on what the compiler will and will not let you do with a `RecordRef`:
[Zero-Copy and Allocation](zero-copy-and-allocation.md).

### Records you will get whether you asked or not

Alongside the schema you subscribed to, the stream carries:

- **`SystemMsg`** — heartbeats arrive here, as ordinary records carrying `SystemCode.Heartbeat`,
  not as control frames. Your loop sees them. Ignore them or log them, but do not be surprised.
- **`ErrorMsg`** — the gateway reporting a problem with a subscription, quoting the `Id` you were
  given back from `SubscribeAsync`.
- **`SymbolMappingMsg`** — `instrument_id` to symbol assignments, sent as they change. Feed these
  to a `PitSymbolMap`; see [Symbol Resolution](symbol-resolution.md).

```csharp
if (record.TryGet(out SystemMsg system) && system.Code == SystemCode.Heartbeat)
{
    continue;
}
```

## Timeouts and heartbeats

`FillBufferAsync` raises `HeartbeatTimeoutException` when nothing arrives within
`EffectiveReadTimeout`, and tears the connection down. That budget resolves in three steps:

```
ReadTimeout            if you set it
HeartbeatInterval + 5s if you set an interval
35s                    otherwise
```

`EffectiveReadTimeout` is public so you can read back what actually applies — a derived setting
whose effect can only be discovered by waiting for it is not a setting.

**Setting `ReadTimeout` shorter than the gateway's heartbeat interval will time out a healthy
connection.** On a quiet feed a heartbeat is the only traffic guaranteed, so a budget below the
interval expires between heartbeats every time. Nothing in the client rejects that combination,
because `HeartbeatInterval` may be left unset — in which case the gateway picks its own interval
and the client has no way to learn what it is. If you shorten the read timeout, set the heartbeat
interval too.

Upstream has no equivalent setting; its timeout is always `interval + 5s`. That derivation is the
right *default* and is what `EffectiveReadTimeout` computes. It is a poor *only* option, because
the budget that matters is a property of the deployment: a quiet overnight replay and a busy
equities open are the same code reading very different streams.

## Reconnecting

```csharp
try
{
    await ReadUntilClosed(client, ct);
}
catch (HeartbeatTimeoutException)
{
    await client.ReconnectAsync(ct);     // connect + authenticate, reusing the resolved address
    await client.ResubscribeAsync(ct);   // replay every subscription
    await client.StartAsync(ct);         // billing resumes here
}
```

`ReconnectAsync` closes whatever is open, reconnects to the **already-resolved** `Endpoint`, and
authenticates. It does not re-resolve DNS, matching upstream's `reconnect()` — which is why
`Endpoint` deliberately survives `CloseAsync`.

`ResubscribeAsync` replays `client.Subscriptions` **with every `Start` cleared**. That is the point
of the method: replaying an intraday-replay start after a reconnect would re-deliver hours of
records you have already seen and already paid for. The clearing happens in the retained list
*before* each line goes out, so a resubscribe that fails half way cannot leave a start behind for
the next attempt to replay.

Neither call is cancel-safe. A resubscribe that fails partway has sent some subscriptions and not
others, on a socket the gateway has stopped reading; the repair is another `ReconnectAsync` and
another `ResubscribeAsync`, which by then has no starts left to drop.

**There is no automatic reconnection.** Whether to retry, how often, and with what backoff are
decisions about your deployment, not about the protocol — and a client that silently reconnected
would silently resume billing.

## Errors

```
Exception
└── LiveException
    ├── LiveConnectException          the attempt failed — refused, unreachable
    │   └── ConnectTimeoutException   ConnectAsync outlived ConnectTimeout
    ├── AuthTimeoutException          the handshake outlived AuthTimeout
    ├── DatabentoAuthenticationException  the gateway rejected the credentials
    ├── HeartbeatTimeoutException     the stream went silent past EffectiveReadTimeout
    └── LiveProtocolException         the gateway sent something that is not the protocol
```

`DbnDecodeException` comes from `DatabentoDotNet.Dbn` and is not a `LiveException`: what the
gateway sent was framed correctly and is not valid DBN. That is a codec problem, not a session
problem.

`InvalidOperationException` means the calls came in the wrong order — subscribing before
authenticating, reading before `StartAsync`. It is a programming error and is not part of the
`LiveException` hierarchy.

[Troubleshooting](troubleshooting.md) maps the common messages to causes.

## Threading

**`LiveClient` is not thread-safe, and deliberately not made so.** One connection is one
conversation with the gateway, and the record loop is a single reader by construction. A lock
around it would advertise a concurrency the protocol does not have.

Read on one thread. If you need to fan work out, hand off the decoded values — not the
`RecordRef`, which the compiler will not let you hand off anyway.

## Testing against a mock

The repository ships `MockLiveGateway`, ported from upstream's own test harness rather than
reinvented. Point a client at it with the `Gateway` property:

```csharp
await using var gateway = new MockLiveGateway("TEST.DATASET");
await using var client = new LiveClient
{
    ApiKey  = new ApiKey(MockLiveGateway.TestApiKey),
    Dataset = gateway.Dataset,
    Gateway = gateway.Address,
};
```

One caveat the repository states about its own harness, and worth repeating: **the mock cannot
confirm what it shares an author with.** It and the client were written from the same reading of
the protocol, so a misreading would sit in both and they would agree with each other. Only a real
gateway settles that.

## See also

- [Timestamps and Prices](timestamps-and-prices.md) — before you compute anything from a record
- [Symbol Resolution](symbol-resolution.md) — `instrument_id` back to a ticker, live
- [Hosting and Dependency Injection](hosting-and-dependency-injection.md) — this client as a hosted service: registration, `IConfiguration` binding, bounded reconnection, health checks and metrics
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — the guarantee and its rules
- [`ROADMAP.md` §4](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — the design decisions behind this client
- [`PORTING.md` §4](https://github.com/jerbersoft/databentodotnet/blob/master/PORTING.md) — where this deviates from `databento-rs`, and why
