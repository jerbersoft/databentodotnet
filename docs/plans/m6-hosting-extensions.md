# M6 — Hosting extensions

> Design document. Working material, not published documentation — `docs/docfx.json` enumerates
> content explicitly so nothing in `plans/` reaches the site. Written 2026-08-31.

## BLUF

A fifth package, `DatabentoDotNet.Extensions.Hosting`, registers all three clients with
`IServiceCollection` and runs a live session as a hosted service — configured from
`appsettings.json`, validated at startup, reconnecting with bounded backoff, and allocating nothing
per record. It ships as **1.1.0**, after 1.0. One additive change it depends on ships **in 1.0**: an
`HttpMessageHandler` seam on `HistoricalClient`.

---

## 1. Why this exists, and what it is not

Nothing in the four packages is hostile to a container, but nothing in them is shaped for one
either. A consumer wiring `HistoricalClient` into an ASP.NET Core app today writes the singleton
registration themselves, reads the API key themselves, and — for live — writes the whole
connect/authenticate/subscribe/start/drain/reconnect loop themselves. That last one is not
boilerplate. It is the part with the failure modes.

**It is not a convenience wrapper around the clients.** The registration methods are the small half.
The package exists for `LiveSessionRunner`: the loop, the reconnect policy, the shutdown behaviour,
and the guarantee that none of it allocates per record.

### The clients are not equally container-shaped, and the design follows that

| Client | Today | Consequence here |
|---|---|---|
| `HistoricalClient` | `required` init properties, builds its own `HttpClient` lazily, documented safe for concurrent requests | Singleton. Needs an HTTP seam it does not have — §2 |
| `ReferenceClient` | Same shape, plus `ReferenceClient(HistoricalClient)` for a shared pool | Singleton over the same transport. **Needs no change at all** |
| `LiveClient` | `IAsyncDisposable`, explicitly *not* thread-safe, four-step lifecycle, `StartAsync` begins billing, zero-copy `RecordRef` loop | Never registered as a resolvable service. Owned by the hosted service — §5 |

That last row is the whole design problem. Registering `LiveClient` in a container is trivial and
wrong: a singleton socket with a billing trigger, handed to whoever resolves it, is a footgun with
an invoice attached.

`AddDatabentoReference()` registers the historical transport itself if `AddDatabentoHistorical()`
has not already done so, and reuses it if it has. Calling both in either order yields one
`HttpClient` and one connection pool; neither call is a prerequisite of the other.

---

## 2. One additive change to a shipped package, in 1.0

It breaks nothing, costs a `PublicAPI.Unshipped.txt` entry, and gets its own issue against the
package it changes. **It goes into 1.0 because it is core surface**, and 1.0 is where the core
surface is settled; the extensions package that consumes it follows in 1.1.

It was found by designing this package, before a line of it was written. That is the evidence
[#68] said it was waiting for — *"nothing has yet built against this library in anger, so there is
no evidence that surface is the right one"* — arriving in the form [#68] described.

### `HttpMessageHandler` seam on `HistoricalClient` (`area: historical`)

```csharp
public HttpMessageHandler? Handler { get; init; }
public bool DisposesHandler { get; init; } = true;
```

`CreateHttpClient()` uses the supplied handler when there is one, and keeps doing everything else it
does today: `BaseAddress`, HTTP Basic from the `ApiKey`, the validated `User-Agent`, the `Accept`
header, and the infinite `Timeout`. **The key still reaches the wire from exactly one place**, which
is the property that made the full-`HttpClient` alternative unattractive — see §9.

The motivating defect is real and is not about socket exhaustion. `new HttpClient()` uses
`SocketsHttpHandler` with `PooledConnectionLifetime` defaulted to infinite, so a singleton in a host
that stays up for weeks keeps talking to whatever IP `hist.databento.com` resolved to on the first
request. `IHttpClientFactory` exists to fix that, and today no part of the surface can reach it.

**`ReferenceClient` needs nothing.** It already has `ReferenceClient(HistoricalClient transport)`,
documented as being "for a consumer holding both" so that two clients do not open two connection
pools to one origin. The extensions package builds one seamed `HistoricalClient` and hands it over.
That constructor was written for this consumer before this consumer existed.

A second change was proposed here: a wire-string parse counterpart for `Schema` and `SType`, on the
claim that `Enums/WireStrings.cs` had `ToWireString` with no parse in the other direction. That
claim was false — `WireStrings.TryParseSchema` and `TryParseSType` already exist, are public, are in
`src/DatabentoDotNet.Dbn/PublicAPI.Unshipped.txt`, and are round-tripped by
`EnumWireStringTests.cs:309-313`. `LiveSessionResolver` calls them directly — §4.

---

## 3. Package boundary

One project. One package. It references all four.

```
src/DatabentoDotNet.Extensions.Hosting/
  ServiceCollectionExtensions.cs       AddDatabento / …Historical / …Reference / …Live
  DatabentoLiveBuilder.cs              .AddRecordHandler<T>(), .AddHealthCheck()
  ILiveRecordHandler.cs                the dispatch contract — §4
  Options/DatabentoOptions.cs          root: key, gateway defaults
  Options/HistoricalOptions.cs
  Options/LiveSessionOptions.cs        + SubscriptionOptions, ReconnectOptions
  Options/LiveSessionResolver.cs       the one conversion path — §4
  Options/LiveSessionValidator.cs      IValidateOptions, calls the resolver
  LiveSessionRunner.cs                 the entire loop — no host, no container
  ReconnectSupervisor.cs               backoff, jitter, bounded attempts
  LiveSessionService.cs                BackgroundService; thin by construction
  Internal/ExtensionsLog.cs            [LoggerMessage] partials, stable event ids
  HealthChecks/LiveSessionHealthCheck.cs
  PublicAPI.Shipped.txt                empty until the surface is promoted
  PublicAPI.Unshipped.txt
  README.md                            its own, per the #74 convention
tests/DatabentoDotNet.Extensions.Hosting.Tests/
samples/DatabentoDotNet.Samples.HostedLive/
```

**`LiveSessionRunner` and `ReconnectSupervisor` are public, not `Internal/`.** §5 and §7 rest on
`LiveSessionRunnerTests` driving the runner directly over `MockLiveGateway` — "no host and no
container" — and this repo declares no `InternalsVisibleTo` anywhere, so a runner a test cannot
reach contradicts §5's own argument. `ExtensionsLog` stays internal: it is `[LoggerMessage]`
partials, exactly as `Historical/Internal/HistoricalLog.cs` is, and nothing outside the assembly
calls it.

`Directory.Build.targets` picks the project up as a `$(ShippingProject)` automatically, which brings
`IsAotCompatible`, the trim and AOT analyzers, and the RS0016 baseline requirement with it. Nothing
needs configuring for that; it needs the two `PublicAPI.*.txt` files to exist or the build fails by
design with a message saying so.

### Naming

Package id `DatabentoDotNet.Extensions.Hosting`, because a hosted live service is the capability
that justifies a package — DI registration alone would not. `Add*` methods live in namespace
`Microsoft.Extensions.DependencyInjection` so they appear on `IServiceCollection` with no extra
`using`, per the near-universal convention. Everything else is in
`DatabentoDotNet.Extensions.Hosting`.

`DatabentoDotNet.*` throughout, never `Databento.*` — the prefix is reserved to `jerbersoft` as of
2026-08-31 and the vendor's namespace is not ours to take.

### Two costs, stated rather than discovered

**Installing it to register `HistoricalClient` in a web API also pulls Live, Reference and Dbn.**
That is the price of the one-package decision. Splitting into `Extensions.Http` and
`Extensions.Hosting` was considered and rejected: two baselines, two READMEs and two guides, to
save a transitive reference to packages that are pure managed, AOT-clean and small.

**New transitive dependencies, for consumers of this package only.** The core four keep their
eight-package closure exactly as [#71] and [#74] verified it:

| Added | For |
|---|---|
| `Microsoft.Extensions.Options.ConfigurationExtensions` | `BindConfiguration`, named options |
| `Microsoft.Extensions.Hosting.Abstractions` | `BackgroundService`, `IHostApplicationLifetime` |
| `Microsoft.Extensions.Http` | `IHttpMessageHandlerFactory` |
| `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions` | the opt-in health check |

`Microsoft.Extensions.DependencyInjection.Abstractions` and `Logging.Abstractions` are already in
the closure and cost nothing new.

---

## 4. Public surface

### Registration is always named

```csharp
services.AddDatabento(builder.Configuration.GetSection("Databento"));

services.AddDatabentoHistorical();                    // binds Databento:Historical
services.AddDatabentoReference();                     // reuses Historical's transport
services.AddDatabentoLive("equities")                 // binds Databento:Live:equities
        .AddRecordHandler<EquityHandler>();
```

`LiveClient.Dataset` is `required`, so one client is one dataset, and a desk watching `GLBX.MDP3`
futures alongside `EQUS.MINI` equities needs two sessions with two handlers and two independent
reconnect states in one host. Names are how that works, and they are in from the start because
retrofitting them after the surface is locked means an overload on every method, both of which then
live in the docs forever.

The no-name overload uses the literal name `"Default"`, so the configuration path is
`Databento:Live:{name}` in every case. The alternative — the single session at `Databento:Live` and
named ones beneath it — makes `Databento:Live:Dataset` and `Databento:Live:equities` siblings of
different kinds, which is ambiguous to read and worse to error on.

**Sessions are declared in code and never conjured from configuration keys.** A session that exists
because somebody added a JSON key, with no handler registered anywhere, fails at startup with a
cause that reads like a bug in this package. Configuration supplies values for sessions that
`AddDatabentoLive` declared; the optional lambda overrides them.

> **Completed by #99.** The lambda above shipped only in its named form, so configuring the
> *default* session in code meant writing `DatabentoLiveBuilder.DefaultSessionName` out — naming the
> one session whose point is not needing a name. `AddDatabentoLive(Action<LiveSessionOptions>)`
> closes that, and #99 also settled the question this section left open by omission:
> **`AddDatabentoReference` gets no lambda overload.** Every other `Add*` here has one because each
> owns an options type; that one owns none — its client is `ReferenceClient(HistoricalClient)` over
> the transport `Databento:Historical` already configures. An overload taking
> `Action<HistoricalOptions>` would be a second name for `AddDatabentoHistorical`'s over the same
> options instance, so calling both would read as two configurations and be one. A `ReferenceOptions`
> of its own would be an empty class advertising a surface with nothing in it. A reference-only
> setting, if one ever exists, brings the overload with it.

### Options are bindable primitives

```jsonc
{
  "Databento": {
    "ApiKey": "…",
    "Historical": { "PooledConnectionLifetime": "PT5M" },
    "Live": {
      "equities": {
        "Dataset": "EQUS.MINI",
        "Subscriptions": [
          { "Schema": "mbp-1", "StypeIn": "raw_symbol", "Symbols": ["AAPL", "MSFT"] }
        ],
        "Reconnect": {
          "Enabled": true, "InitialDelay": "PT1S", "MaxDelay": "PT30S", "MaxAttempts": 10
        }
      }
    }
  }
}
```

Every option is a `string`, `int`, `bool` or a list of those. Two constraints force this and both
are absolute:

- **`T:System.TimeSpan` is banned as a type, not merely by its members.** `BannedSymbols.txt` bans
  the type, so RS0030 fires on a `TimeSpan` property declaration, not just on `TimeSpan.FromSeconds`.
  An options DTO cannot have one.
- **`Duration` is not bindable.** NodaTime's `Duration` has no `TypeConverter` and no settable
  properties for a binder to fill, so it binds to nothing or to garbage.

ISO-8601 strings parsed by `PeriodPattern.NormalizingIso` — `.Parse(text).Value.ToDuration()` — is
what is left, and it is a good answer rather than a residual one: `"PT30S"` is unambiguous across
locales, which `InvariantGlobalization` in this repo makes a live concern. Two guards run before
`ToDuration()`: a period with non-zero months or years parses but then throws
`InvalidOperationException` from NodaTime rather than failing with a message that names the
configuration path, so it is rejected first; a negative duration parses cleanly and is meaningless
for a delay or a backoff bound, so it is rejected too.

The same reasoning covers the library's own types. `ApiKey` validates in its constructor and has no
parameterless form, and `Symbols` has no binder-shaped form at all. `Schema` and `SType` are the
narrower case: an enum binds fine, but only by its C# name, which is the wrong spelling — §2.
All four are resolved, not bound.

### One conversion path, shared by validation and construction

`LiveSessionResolver` turns a `LiveSessionOptions` into an immutable `ResolvedLiveSession` carrying
the real types — `ApiKey`, `Duration`, `Schema`, `Symbols`, `Instant?`. `LiveSessionValidator`
(`IValidateOptions<LiveSessionOptions>`) calls the resolver at startup and reports every failure it
finds, each naming its configuration path:

```
Databento:Live:equities:Subscriptions:0:Schema — 'mbp1' is not a Databento schema.
```

**There is exactly one crossing, and both callers use it.** A configuration that validates is a
configuration that resolves, because no second path exists to disagree. This is the rule `DbnTime`
already enforces for the `UndefTimestamp` sentinel — "do not add a second conversion path that skips
the check" — applied to a different boundary for the same reason.

### API key precedence

Session options → root options → the `DATABENTO_API_KEY` environment variable.

That last step answers ROADMAP §10 open question 5 for the hosting case, and matches all four
samples and the other Databento clients. It is a second mechanism alongside the configuration
provider's own `Databento__ApiKey`, which is a real cost; it is paid because a reader who has run
any Databento sample has that variable set and will expect it to work.

---

## 5. The live session runner

### `LiveSessionRunner` holds the loop; `LiveSessionService` holds almost nothing

This is CLAUDE.md's own doctrine applied. `RealGatewayLatencyTests` is 180 lines of session setup
and three assertions because the collection loop, the exclusion rule, the clock arithmetic and the
report all live in `LatencyMeasurement`, which `MockLiveGateway` drives on every `dotnet test`. Same
split: the runner takes a `ResolvedLiveSession` and an `ILiveRecordHandler` and needs no host and no
container, so `MockLiveGateway` drives all of it for free. `LiveSessionService` resolves options and
calls the runner, and has nothing left in it worth a test.

### Startup splits by exception type

Connect, authenticate and subscribe run during host startup, so a wrong key fails the boot rather
than a background task nobody is watching. Then:

- **`DatabentoAuthenticationException` is fatal.** Retrying a wrong key bills nothing and fixes
  nothing. The host fails to start.
- **`LiveConnectException`, `ConnectTimeoutException`, `AuthTimeoutException` are transient.** Into
  the backoff.

Both categories are already distinct types in `DatabentoDotNet.Live`; this rule reads them rather
than inventing a taxonomy.

### Dispatch: synchronous, `ref struct`, zero allocation

```csharp
public interface ILiveRecordHandler
{
    /// Called once per record, inside the drain. The RecordRef is valid for this call only.
    void OnRecord(scoped RecordRef record);

    /// Called once per socket fill, after the drain. Where I/O goes.
    ValueTask OnFlushAsync(CancellationToken cancellationToken);
}
```

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    while (client.TryNextRecord(out RecordRef record))
        handler.OnRecord(record);

    await handler.OnFlushAsync(cancellationToken);
    if (await client.FillBufferAsync(cancellationToken) == 0) break;
}
```

The async-per-record alternative — `RecordsAsync`, two allocations per record — was rejected as the
*primary* contract because it gives up the guarantee `LiveAllocationTests` asserts, in the one
package whose reason to exist is that guarantee. It remains available: `RecordsAsync` is public on
`LiveClient` and a caller who wants it does not need this package's help.

Three consequences, each a deliberate call:

- **The handler is a singleton.** A DI scope per record would allocate and defeat the contract. The
  guide shows opening a scope inside `OnFlushAsync` for callers needing scoped services.
- **A handler exception is fatal to the session.** Swallowing it loses market data invisibly, which
  is the failure class this codebase exists to convert into loud ones. A handler that wants to
  continue catches its own.
- **`OnFlushAsync` is where batching lives.** Awaiting an already-completed `ValueTask` allocates
  nothing, so a handler with nothing to flush costs nothing.

### Reconnection, and why this is not the thing PORTING.md forbids

PORTING.md §4 says it twice: *"`reconnect` and `resubscribe` are deliberately separate. Replaying
subscriptions is the caller's decision; do not fuse them into an auto-reconnect."*

That is a rule about `LiveClient`, and **a hosted service is precisely the caller it defers to.**
The library still does not fuse them; this package makes the caller's decision explicitly, in one
place, with a bound on it. Recording the distinction here because it reads like a contradiction and
someone will eventually raise it as one.

On a transient failure: delay, `ReconnectAsync`, `ResubscribeAsync`, `StartAsync`, resume the loop.
That order matters — `ResubscribeAsync` clears each subscription's `start`, so a reconnect does not
replay the same history twice (PORTING.md:1256).

- Exponential backoff from `InitialDelay` to `MaxDelay`, bounded by `MaxAttempts`. **Jitter is
  applied and is not configurable** — its only purpose is to stop a restarted fleet reconnecting in
  lockstep, and a knob for that is a knob whose correct value is never anything but "on".
- **`MaxAttempts` bounds *consecutive* failures**; the counter resets on a successful `StartAsync`.
  A gateway that flaps every ten minutes therefore reconnects indefinitely. That is deliberate — the
  alternative silently stops a worker overnight — and the guide says so in those words.
- **Every `StartAsync` is a newly billed session.** The guide says that too. A reconnect storm is a
  billing event, not merely a connection event, and `MaxAttempts` is what bounds it.

### Shutdown half-closes

Cancellation breaks the loop, then `CloseAsync` within `CloseTimeout` — the gateway gets to finish,
rather than having the socket dropped on it.

> **Corrected by #98.** This read "within a bounded slice of the host's shutdown timeout", which was
> never what shipped: nothing derived the ceiling from `HostOptions.ShutdownTimeout`, and until #98
> no configuration key reached it at all, so every hosted session got a fixed five seconds. The
> ceiling is now `{section}:Live:{name}:CloseTimeout`, defaulting to five seconds. Deriving it from
> the host's budget was considered and rejected: `HostOptions` lives in
> `Microsoft.Extensions.Hosting`, and taking that dependency on the whole package to read one
> property is a poor trade for a library that otherwise needs only the abstractions.

---

## 6. Observability

**Metrics count locally and publish at flush.** A `Counter<long>.Add` per record is a per-record
cost on the one path that promises none — tagged counters allocate outright. A `long` increment in
the loop and one `Add` per flush reports the same number for nothing. `Meter` name
`DatabentoDotNet.Extensions.Hosting`: records received, reconnect attempts, session starts, flush
duration.

**Logging is `[LoggerMessage]` partials with stable event ids** in `Internal/ExtensionsLog.cs`,
mirroring `Internal/HistoricalLog.cs`, and follows PORTING.md §2's rule — log only what the caller
cannot otherwise see. Session started, reconnect attempted, reconnect exhausted, handler faulted.
**Never per record**, which is both that rule and the allocation guarantee agreeing.

**Health checks are opt-in** through `.AddHealthCheck()` on the builder: `Healthy` once started and
reading, `Degraded` while reconnecting, `Unhealthy` once attempts are exhausted.

---

## 7. Testing

The runner is driven directly by `MockLiveGateway`, with no host and no container. That is most of
the suite, and it is possible because §5 put the loop somewhere a test can reach.

| Test | Settles |
|---|---|
| `LiveSessionRunnerTests` | The loop, over the mock: drain order, flush points, stream end |
| `LiveSessionReconnectTests` | Reconnect → resubscribe → restart ordering, backoff timing, `MaxAttempts` stopping it, the counter resetting on success |
| `RegistrationTests` | A real `ServiceProvider` resolves what was registered; two named sessions stay independent |
| `OptionsValidationTests` | Every failure names its configuration path; `"mbp-1"` reaches `Schema.Mbp1` |
| `ExtensionsAllocationTests` | **Zero bytes per record** through the runner, plus the deliberate-allocation counter-test both existing allocation files carry |
| `AotProbe` (extended) | ILC accepts a `HostApplicationBuilder`, the container and the binding generator — §8 |

**No new billable test, and CLAUDE.md's free/billable table is unchanged.** Everything above is
settled by the mock. The fact only a real session can establish is already owned by
`RealGatewaySessionTests`; running a second one here would spend money to learn nothing new, which
is the inverse of the rule that "the expensive run is for the fact only it can settle".

### The mock's known limit still applies

CLAUDE.md: *"The mock cannot confirm what it shares an author with."* That is unchanged and
undiminished here — but it also does not grow. This package adds no new reading of
`live/protocol.rs`; it composes calls whose protocol correctness `MockLiveGateway` and
`RealGatewaySessionTests` already established between them.

---

## 8. Native AOT

`$(ShippingProject)` turns on `IsAotCompatible`, `EnableTrimAnalyzer` and `EnableAotAnalyzer`
automatically, and `TreatWarningsAsErrors` makes each an error. Two things follow.

**Configuration binding must use the source generator.** `ConfigurationBinder.Bind` and the
reflection-based `Configure<T>(IConfiguration)` are annotated `RequiresUnreferencedCode` /
`RequiresDynamicCode`, so they are IL2026/IL3050 and therefore build errors here.
`EnableConfigurationBindingGenerator=true` intercepts those calls with generated code, and this was
verified in a library carrying `IsAotCompatible`, `EnableTrimAnalyzer`, `EnableAotAnalyzer` and
`TreatWarningsAsErrors` rather than assumed: `true` builds with zero warnings and zero errors,
`false` produces six — IL2026 ×3, IL3050 ×3 — across all three call shapes this package uses
(`OptionsBuilder<T>.Bind(IConfiguration)`, `services.Configure<T>(name, section)`,
`OptionsBuilder<T>.BindConfiguration(path)`). Bound at runtime as well as compiled: the §4 JSON
shape, nested `Subscriptions` object list and `Symbols` string list included, round-trips correctly.
There is no fallback binder and no spike task; the property is set in Task 3, with a guard test in
the same task keeping it set.

**The AOT probe gets extended, and that is a real question rather than a formality.**
`MockLiveGateway` is already linked into `tools/DatabentoDotNet.AotProbe` by `<Compile Link>`, so the
probe can build a `HostApplicationBuilder`, register a session against the mock, run it and assert
records arrived. Whether a DI container, the generic host and generated binder survive ILC is not
answerable by the analyzers: ILC scans IL, and the probe exists precisely because
"an analyzer is not a verification" ([#64]). A reference nothing calls is trimmed away and proves
nothing, so the probe must *drive* the host, not merely link the package.

**`PooledConnectionLifetime` is set as `Duration.FromMinutes(5).ToTimeSpan()`.** The property's type
is `TimeSpan`, which is banned — but RS0030 fires on the type being *named in source*, and this form
never names it. The same trick `HistoricalClient` already uses for
`Timeout.InfiniteTimeSpan`.

---

## 9. Rejected alternatives

Recorded because CLAUDE.md asks for it: *"The most valuable sentences in this project's
documentation are the ones explaining why something is not there."*

**A full `HttpClient` seam instead of an `HttpMessageHandler` one.** It makes
`AddHttpClient<HistoricalClient>()` work in the textbook way, and it loses the property that the API
key reaches the wire from exactly one place. Either the client mutates an object it does not own to
attach the `Authorization` header, or the caller attaches it — and then the key has two paths to the
wire, which is precisely what `ApiKey`'s redacted `ToString` and the single-header rule exist to
prevent.

**Async-per-record as the primary dispatch contract.** Two allocations per record, in the package
whose parent library asserts exactly zero. Available to anyone as `LiveClient.RecordsAsync`, which
needs no help from this package.

**A `Channel<T>` pump between the socket and the consumer.** Familiar and it decouples a slow
consumer from the socket — but `LiveClient` already models slow readers through
`SlowReaderBehavior`, and a queue in front of that is a second backpressure mechanism with different
semantics, in front of one the gateway already participates in.

**Splitting into `Extensions.Http` and `Extensions.Hosting`.** Two baselines, two READMEs and two
guides, to save a transitive reference to packages that are small, pure managed and AOT-clean.

**A wire-string parser inside the extensions package.** A second copy of the schema table, and the
copy that silently disagrees is the one nobody is looking at. `WireStrings.TryParseSchema` and
`TryParseSType` already exist beside `ToWireString` — §2 — and `LiveSessionResolver` calls them
directly rather than reimplementing them.

**Auto-registering live sessions found in configuration.** A session declared by a JSON key with no
handler registered fails at startup with a cause that reads like a bug in this package.

**Everything in 1.0.** Five packages locked together would give the extensions surface a SemVer
promise on its first day of existence, which is exactly what [#68] refused to do for the core four
after a full milestone of work. It gets its own evidence window instead.

---

## 10. Issues

`M6: Hosting extensions` and `area: extensions` do not exist yet. Both are prerequisites, along with
a ROADMAP §8 entry — CLAUDE.md requires a milestone and an area label on every issue, so this is
setup rather than paperwork.

| # | Issue | Milestone | Labels |
|---|---|---|---|
| 0 | Create `M6` milestone, `area: extensions` label, ROADMAP §8 section | — | — |
| 1 | `HttpMessageHandler` seam on `HistoricalClient` | M5 | `type: feature`, `area: historical` |
| 2 | Project, options model, resolver, validation, registration | M6 | `type: feature`, `area: extensions`, `area: build` |
| 3 | `LiveSessionRunner`, `ILiveRecordHandler`, the hosted service | M6 | `type: feature`, `area: extensions`, `area: live` |
| 4 | Reconnect supervisor and its backoff | M6 | `type: feature`, `area: extensions`, `area: live` |
| 5 | Health checks and metrics | M6 | `type: feature`, `area: extensions` |
| 6 | Extend the AOT probe to drive a host | M6 | `type: chore`, `area: extensions`, `area: build` |
| 7 | Guide, package README, and the `HostedLive` sample | M6 | `type: docs`, `area: extensions` |

Issue 1 is a 1.0 blocker and gates everything below it. Issue 2 gates 3; 3 gates 4, 5 and 6.
Issue 7 lands last, because CLAUDE.md requires a behaviour change and its guide in one commit and
the behaviour is not settled until 6 passes.

### Release sequence

```
0.9.x   beta, as today
  ↓
1.0.0   four packages, surface locked, + issue 1
  ↓
1.1.0   DatabentoDotNet.Extensions.Hosting
```

The package's `PublicAPI.Shipped.txt` stays empty through 1.1.0, for the same reason the core four's
did through 0.9.x: `Shipped` lists a surface we have undertaken not to break, and that undertaking
should follow evidence rather than precede it.
