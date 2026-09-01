# Hosting and Dependency Injection

**`AddDatabento` binds the `Databento` configuration section and registers nothing else;
`AddDatabentoHistorical` and `AddDatabentoReference` add the two HTTP clients on
`IServiceCollection`, and `AddDatabentoLive` runs one named live session as a hosted service, with
bounded reconnection, an opt-in health check, and metrics built in.** This page covers
`DatabentoDotNet.Extensions.Hosting` end to end: registration, the configuration shape, writing a
handler, running more than one session, and what a hosted session does when the gateway drops.

`AddDatabento` on its own gives you no clients — it is the section marker every other call reads,
and the three `Add*` calls below are what register something you can resolve. Calling only
`AddDatabento()` and then asking for a `HistoricalClient` is `No service for type
'DatabentoDotNet.Historical.HistoricalClient' has been registered`.

For the client underneath the hosted service, see [Live Streaming](live-streaming.md) — this page
does not repeat the session lifecycle, the record loop, or the timeout rules, all of which apply
unchanged to a session run this way.

> [!NOTE]
> This page describes `DatabentoDotNet.Extensions.Hosting` **1.1.0**. The package ships after
> `1.0`, deliberately: locking five packages together on day one would give this one's surface a
> SemVer promise before anything had built against it. See `ROADMAP.md` §8.

---

## Install

```sh
dotnet add package DatabentoDotNet.Extensions.Hosting
```

One package reference brings all four core packages with it —
`DatabentoDotNet.Dbn`, `.Live`, `.Historical` and `.Reference` — because registering
`HistoricalClient` in a web API and never touching the live client is a legitimate way to use this
package, and a split into an HTTP-only package and a live-hosting package was rejected: two
baselines, two READMEs and two guides, to save a transitive reference to four packages that are
small, pure managed, and AOT-clean.

## The shortest thing that works

A minimal session, using the default name and the parameterless registration:

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDatabento();
builder.Services.AddDatabentoLive().AddRecordHandler<TradePrinter>();

using var host = builder.Build();
await host.RunAsync();

internal sealed class TradePrinter : ILiveRecordHandler
{
    public void OnRecord(scoped RecordRef record)
    {
        if (record.TryGet(out TradeMsg trade))
        {
            Console.WriteLine($"{record.Header.InstrumentId} {trade.Price} x {trade.Size}");
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

```json
{
  "Databento": {
    "Live": {
      "Default": {
        "Dataset": "EQUS.MINI",
        "Subscriptions": [
          { "Schema": "trades", "Symbols": ["AAPL", "MSFT"] }
        ]
      }
    }
  }
}
```

No `ApiKey` anywhere in that file. `LiveSessionResolver` looks for one in three places, in order:
the session's own `ApiKey`, then `Databento:ApiKey`, then the `DATABENTO_API_KEY` environment
variable — so exporting the variable is enough to run the example above. A configuration that
supplies none of the three fails at startup (`ValidateOnStart`), naming all three places it looked.

`Default` is a literal, not a placeholder — it is `DatabentoLiveBuilder.DefaultSessionName`, and
the parameterless `AddDatabentoLive()` always binds it. That keeps every session's configuration at
`Databento:Live:{name}`, named or not, rather than making the unnamed case a special shape.

## Registering the historical and reference clients

```csharp
builder.Services.AddDatabento();
builder.Services.AddDatabentoHistorical();
builder.Services.AddDatabentoReference();
```

Both are ordinary singletons, not hosted services — there is no session lifecycle to run, so
nothing here needs a `BackgroundService`. `AddDatabentoReference` composes with
`AddDatabentoHistorical` in either order: whichever runs first registers the shared
`HistoricalClient`, and the other reuses it, so calling both — directly or because your code and a
library you depend on each call one — still yields one client, one connection pool, and no
disposal race. See <xref:DatabentoDotNet.Extensions.Hosting.HistoricalOptions> for
`PooledConnectionLifetime`, the one setting that exists because a singleton `HistoricalClient` in a
host that stays up for weeks would otherwise keep talking to whatever address it resolved on its
first request.

`AddDatabentoHistorical` takes the same lambda-overload pattern as `AddDatabentoLive` below,
applied after binding: `AddDatabentoHistorical(options => options.UserAgentExtension =
"my-app/1.0")`. **`AddDatabentoReference` has no lambda overload** — it configures nothing of its
own, since the reference client shares `Databento:Historical` with the historical client, so
configure both through `AddDatabentoHistorical`'s.

Both calls are idempotent, in either order and however many times: one `HistoricalClient`, one
named `HttpClient`, one connection pool.

### Reaching the transport

The `HttpClient` those two share is registered with `IHttpClientFactory` under the name
`DatabentoServiceCollectionExtensions.HttpClientName` — the string `DatabentoDotNet.Historical`.
That is a public constant because the standard way to layer a proxy, a corporate
`HttpMessageHandler`, or a resilience policy onto a factory registration has no form that does not
name the client:

```csharp
builder.Services.AddDatabentoHistorical();
builder.Services.AddHttpClient(DatabentoServiceCollectionExtensions.HttpClientName)
       .AddHttpMessageHandler<CorrelationIdHandler>();
```

The same call is where `AddStandardResilienceHandler()` goes if you have
`Microsoft.Extensions.Http.Resilience` installed. Guessing the string instead of using the constant
fails silently rather than loudly: it configures a second, unused client that nothing resolves.

One thing not to do on that name: `ConfigurePrimaryHttpMessageHandler` *replaces* the
`SocketsHttpHandler` this package installs, and with it the `PooledConnectionLifetime` rotation
that is the whole reason the registration exists. Add delegating handlers, or set
`PooledConnectionLifetime` yourself on whatever you put there.

## Configuration reference

### The `Databento` root is a default, not a fixture

Every heading below is written `Databento:…` because that is what `AddDatabento()` binds from. It
is a default. `AddDatabento` takes a path, or an `IConfigurationSection`, when your configuration
puts these keys somewhere else:

```csharp
builder.Services.AddDatabento("MyApp:Feeds");
builder.Services.AddDatabento(builder.Configuration.GetSection("MyApp:Feeds"));
```

Everything below then hangs off that root instead — `MyApp:Feeds:Historical`,
`MyApp:Feeds:Live:equities` — **including the paths in startup-failure messages**, which name the
section you registered and never the literal `Databento`. A message naming a key that is not in
your file would be worse than one naming no key at all: it sends you looking.

The overloads are equivalent: the section form reads the section's `Path` and discards the rest,
because the binding happens when the options are built and resolves its own `IConfiguration` from
the container then.

**Call it before the other `Add*` methods.** Each of them reads the root at the moment you call it,
so `AddDatabentoHistorical()` before `AddDatabento("MyApp:Feeds")` binds the historical section from
`Databento:Historical` — the fallback for a host that never registered a root at all. Nothing warns
about it, because a package cannot tell that ordering apart from a deliberate standalone
`AddDatabentoHistorical()`.

Every duration below is an **ISO-8601 duration**, not a `TimeSpan` shorthand — `"30s"` and
`"00:00:30"` both fail to parse and are reported as a startup failure naming the configuration path
that held them. `PT30S` is thirty seconds, `PT5M` is five minutes, `PT1H30M` is one hour and thirty
minutes. The `T` matters: it separates the date part (years, months, weeks, days) from the time
part (hours, minutes, seconds), which is how `P1M` (one month) and `PT1M` (one minute) are different
strings rather than an ambiguity. A month or year component is rejected outright — a month has no
fixed length, so it cannot become the `Duration` these values resolve to.

### `Databento` — root

| Key | Type | Default | Notes |
|---|---|---|---|
| `ApiKey` | string | none | Used by any client or session that does not carry its own. Checked after a session's own key and before `DATABENTO_API_KEY`. |

### `Databento:Historical` — the shared historical/reference transport

| Key | Type | Default | Notes |
|---|---|---|---|
| `ApiKey` | string | the root's | |
| `BaseUrl` | string (URL) | Databento's gateway | For a proxy or a test harness |
| `UserAgentExtension` | string | none | Appended to this library's own `User-Agent` |
| `PooledConnectionLifetime` | ISO-8601 duration | `PT5M` | How long a pooled connection is reused before rotation |

### `Databento:Live:{name}` — one session

| Key | Type | Default | Notes |
|---|---|---|---|
| `ApiKey` | string | the root's, then `DATABENTO_API_KEY` | |
| `Dataset` | string | *required* | Wire name, e.g. `EQUS.MINI`, `GLBX.MDP3` |
| `Subscriptions` | array | *required*, at least one | See below |
| `Reconnect` | object | see below | |
| `SendTsOut` | bool | `false` | Ask the gateway to stamp each record with its send time |
| `Compression` | string | `none` | `none` or `zstd` |
| `SlowReaderBehavior` | string | the gateway's default | `warn` or `skip` |
| `HeartbeatInterval` | ISO-8601 duration | the gateway's default | 5–1800 seconds; see [Live Streaming](live-streaming.md#timeouts-and-heartbeats) |
| `ReadTimeout` | ISO-8601 duration | derived from the heartbeat interval | Same page |
| `Gateway` | `host:port` | derived from `Dataset` | Override, e.g. to point a test at a mock |

### `Databento:Live:{name}:Subscriptions[]`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Schema` | string | *required* | Wire spelling, e.g. `trades`, `mbp-1`, `ohlcv-1s` |
| `StypeIn` | string | `raw_symbol` | Wire spelling, e.g. `parent`, `continuous`, `instrument_id` |
| `Symbols` | array of string | *required* | Or a single entry of `ALL_SYMBOLS` for the whole dataset |
| `Start` | ISO-8601 instant | none | Intraday replay, e.g. `2024-01-01T00:00:00Z` |
| `UseSnapshot` | bool | `false` | Book snapshot first. MBO only, and not with `Start` |

### `Databento:Live:{name}:Reconnect`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `true` | |
| `InitialDelay` | ISO-8601 duration | `PT1S` | The first backoff delay |
| `MaxDelay` | ISO-8601 duration | `PT30S` | The backoff ceiling |
| `MaxAttempts` | int | `10` | *Consecutive* failures tolerated — see [Reconnection](#reconnection) |

A session can also be configured, or overridden after binding, with a lambda:

```csharp
builder.Services.AddDatabentoLive("equities", options => options.Dataset = "XNAS.ITCH");
```

The lambda runs after `BindConfiguration`, so it wins over a bound value — the same order
`AddDatabentoHistorical`'s lambda overload uses.

### What startup validation covers, and what it does not

**"Validated at startup" means every value above was parsed and converted, not that every
constraint in those tables was checked.** `ValidateOnStart` runs `LiveSessionResolver`, which is
the one crossing from these strings to the library's real types, and every failure it reports names
its configuration path:

```
Databento:Live:equities:Subscriptions:0:Schema — 'mbp1' is not a Databento schema.
```

That covers the API key, the dataset, each subscription's schema, symbology and symbol set, every
ISO-8601 duration and instant, `MaxAttempts`, the `InitialDelay` ≤ `MaxDelay` pair, and the
`Gateway` endpoint including its port range.

**Three of the constraints above are enforced by the library rather than by the resolver, and
surface later:**

| Constraint | Checked by | Surfaces as |
|---|---|---|
| `HeartbeatInterval` is 5–1800 seconds | `LiveClient.HeartbeatInterval` | `ArgumentOutOfRangeException` naming the property |
| `ReadTimeout` is positive | `LiveClient.ReadTimeout` | `ArgumentOutOfRangeException` naming the property |
| `UseSnapshot` is `mbo`-only and never with `Start` | `Subscription.Validate` | `ArgumentException` naming the parameter |

The resolver does not re-check them **on purpose**: a second copy of a rule the library already
holds is a copy free to drift from it, and the one that silently disagrees is the one nobody is
looking at. `Subscription.Validate` is `internal` to `DatabentoDotNet.Live` besides, so there is no
delegating to it either.

The practical consequence is narrow. All three still fail the host's boot rather than a background
task — `LiveSessionService.StartAsync` awaits the session's start before `base.StartAsync`, so the
process does not come up reporting itself healthy. What you lose is the configuration path in the
message: the exception names `HeartbeatInterval`, and you have to know it came from
`Databento:Live:{name}:HeartbeatInterval`.

## Writing a handler

```csharp
internal sealed class TradePrinter : ILiveRecordHandler
{
    public void OnRecord(scoped RecordRef record)
    {
        // Copy out what you need. The RecordRef points into the runner's read buffer and is
        // valid for this call only — the next fill may shift it.
        if (record.TryGet(out TradeMsg trade))
        {
            Console.WriteLine($"{record.Header.InstrumentId} {trade.Price} x {trade.Size}");
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

**The rule, stated plainly: copy out what you need in `OnRecord`; the reference is valid for that
call only.** `OnRecord` is synchronous and cannot await anything — a `RecordRef` cannot survive an
`await`, so there is no way to hand it somewhere that could. `OnFlushAsync` runs once per socket
fill, after every buffered record has been drained, and is where I/O belongs.

A handler is registered once and constructed as a **singleton** — a DI scope per record would
allocate, in the one package whose reason to exist is that it does not. A handler that needs a
scoped service (a `DbContext`, a per-request `HttpClient`) opens a scope inside `OnFlushAsync`
itself, batching records in a field between flushes:

```csharp
public sealed class TradeWriter(IServiceScopeFactory scopeFactory) : ILiveRecordHandler
{
    private readonly List<TradeMsg> _batch = [];

    public void OnRecord(scoped RecordRef record)
    {
        if (record.TryGet(out TradeMsg trade))
        {
            _batch.Add(trade);   // TradeMsg is a plain struct — this copies, it does not alias
        }
    }

    public async ValueTask OnFlushAsync(CancellationToken cancellationToken)
    {
        if (_batch.Count == 0)
        {
            return;   // an already-completed ValueTask allocates nothing
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITradeSink>()
                   .WriteAsync(_batch, cancellationToken);

        _batch.Clear();
    }
}
```

An exception from either method ends the session — swallowing one would lose market data
invisibly. A handler that wants to carry on catches its own. Full member documentation, including
why the interface has exactly these two methods and no third:
<xref:DatabentoDotNet.Extensions.Hosting.ILiveRecordHandler>.

## Two sessions in one host

```csharp
builder.Services.AddDatabentoLive("equities").AddRecordHandler<EquityHandler>();
builder.Services.AddDatabentoLive("futures").AddRecordHandler<FutureHandler>();
```

One `LiveClient` is one TCP connection to one dataset — <xref:DatabentoDotNet.Live.LiveClient.Dataset>
is `required` and singular. Streaming two datasets is therefore two sessions, not one session with
two subscriptions, and the registration reflects that: two names, bound from
`Databento:Live:equities` and `Databento:Live:futures`, each with its own handler, its own
`LiveSessionRunner`, and its own independent reconnect state. A gateway drop on one does not touch
the other.

## Reconnection

**On by default.** `ReconnectOptions.Enabled` starts `true`, and a hosted session reconnects on a
transient failure — a dropped connection, a heartbeat timeout, a protocol error — without any code
beyond registration. A rejected API key is not transient and is not retried: retrying a wrong key
bills nothing and fixes nothing.

**Every successful reconnect is a newly billed session, and `MaxAttempts` is what bounds that
cost** — budget for it accordingly. How the bound counts attempts, what resets it, and why the
jitter it applies is not configurable are all on
<xref:DatabentoDotNet.Extensions.Hosting.ReconnectSupervisor>, the type that implements it.

**A clean close is not a failure, and does not reconnect.** When the gateway ends the stream on
purpose, the session moves to `LiveSessionState.Stopped`, not `Faulted`, and nothing retries it —
retrying a deliberate close would turn "the gateway said stop" into "reconnect forever."

## Health checks and metrics

A health check is **opt-in**: nothing in `AddDatabentoLive` registers one, so a consumer who never
calls `AddHealthCheck` pays nothing for it.

```csharp
builder.Services.AddDatabentoLive("equities")
    .AddRecordHandler<EquityHandler>()
    .AddHealthCheck();
```

It reports `LiveSessionRunner.State`, mapped as:

| State | Result |
|---|---|
| `Running` | Healthy |
| `NotStarted`, `Starting` | Degraded — coming up, not yet serving |
| `Reconnecting` | Degraded — the backoff is running and bounded, and most drops recover on the first attempt |
| `Stopped` | Unhealthy — the worker is alive and reading nothing, which is the failure a probe exists to surface |
| `Faulted` | Unhealthy, carrying the fault's message and exception |

Full parameter documentation — the registration name, and `failureStatus` for a session whose loss
should degrade rather than take the process out of rotation — is on
<xref:DatabentoDotNet.Extensions.Hosting.DatabentoLiveBuilder>.

Four instruments publish on every session, on the meter named
`DatabentoDotNet.Extensions.Hosting` — pass that name to `AddMeter` when wiring up OpenTelemetry:

| Instrument | Unit | Reports |
|---|---|---|
| `databento.live.records.received` | `{record}` | Records handed to the handler, once per flush |
| `databento.live.sessions.started` | `{session}` | Sessions opened, including ones re-established by a reconnect |
| `databento.live.reconnects.attempted` | `{attempt}` | Reconnection attempts, successful or not |
| `databento.live.flush.duration` | `ms` | How long `OnFlushAsync` took, once per drained buffer |

Every measurement carries a `databento.session` tag naming the session, so two sessions in one
host are two distinct series on each instrument rather than one that mixes them. Full detail on why
each of these publishes exactly where it does — and why none of them is called once per record — is
on <xref:DatabentoDotNet.Extensions.Hosting.LiveSessionMetrics>.

## What is not here, and never will be

**There is no `Task<RecordRef>`, and there never can be one.** An `async` method cannot return a
`ref struct`, so a per-record `await` is not available at any price — not in this package and not
in `DatabentoDotNet.Live` underneath it. `OnRecord` is synchronous for the same reason
`LiveClient.TryNextRecord` is: the compiler enforces the lifetime this package's own zero-allocation
guarantee depends on. See [Zero-Copy and Allocation](zero-copy-and-allocation.md) for the full
argument.

If that split is more than a given handler wants to think about,
`LiveClient.RecordsAsync` — the plain client's own `IAsyncEnumerable<OwnedRecord>` surface — is
still there and needs no help from this package. It costs two allocations per record. Nothing
prevents building a handler around it; this package's guarantee is simply that its own path, the
one described above, costs none.

## See also

- [Live Streaming](live-streaming.md) — the client a hosted session runs underneath: the CRAM
  handshake, subscriptions, timeouts, and what reconnecting means at the protocol level
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — why `OnRecord` takes a `scoped RecordRef`
  and what the compiler will and will not let you do with one
- <xref:DatabentoDotNet.Extensions.Hosting> — the full API reference for this package
- [`ROADMAP.md` §8](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — why this
  ships as `1.1.0` rather than in `1.0`, and the `HttpMessageHandler` seam `1.0` shipped to enable it
