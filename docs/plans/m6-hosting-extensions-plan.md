# M6 — Hosting extensions: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.
>
> Working material, not published documentation — `docs/docfx.json` enumerates content explicitly
> so nothing in `plans/` reaches the site. Written 2026-08-31.

**Goal:** Ship `DatabentoDotNet.Extensions.Hosting` — a fifth package that registers all three
clients with `IServiceCollection` and runs a live session as a hosted service, configured from
`appsettings.json`, validated at startup, reconnecting with bounded backoff, and allocating nothing
per record.

**Architecture:** One package referencing all four existing ones. Configuration binds to
all-primitive DTOs through the configuration binding source generator; a single
`LiveSessionResolver` turns those into a `ResolvedLiveSession` carrying real types, and both
startup validation and session construction go through it, so a configuration that validates is a
configuration that resolves. `LiveSessionRunner` owns the connect/authenticate/subscribe/start/
drain/reconnect loop and needs neither a host nor a container, so `MockLiveGateway` drives all of
it on every `dotnet test`; `LiveSessionService` is a `BackgroundService` that resolves a runner and
calls it.

**Tech Stack:** .NET 10, C# latest, NodaTime, xunit.v3, `Microsoft.Extensions.*` (Options,
Options.ConfigurationExtensions, Hosting.Abstractions, Http, Diagnostics.HealthChecks),
`System.Diagnostics.Metrics`, DocFX.

**Spec:** [`docs/plans/m6-hosting-extensions.md`](m6-hosting-extensions.md) — read it first. This
plan argues from it and corrects it in four places, listed immediately below.

---

## Spec corrections established before this plan was written

Each was checked by running something, not by reading. **Task 1 folds all four back into the spec**
so the two documents do not disagree.

### C1 — Spec §2b is obsolete. `TryParseSchema` and `TryParseSType` already exist.

§2b proposes adding `TryFromWireString` for `Schema` and `SType`, on the claim that
`Enums/WireStrings.cs` "has `ToWireString` for seven enums and **no parse in the other direction**".
That is false. `WireStrings.cs` already carries a `TryParse{Enum}` for **all seven** string-valued
enums, they are public, they are in `src/DatabentoDotNet.Dbn/PublicAPI.Unshipped.txt:1436-1442`, and
`EnumWireStringTests.cs:309-313` already round-trips every variant of `SType` and `Schema` against
`ToWireString` — which is exactly the test §2b proposed to write.

```
src/DatabentoDotNet.Dbn/Enums/WireStrings.cs
  static bool TryParseSType(string? value, out SType result)     // + 4 legacy aliases
  static bool TryParseSchema(string? value, out Schema result)   // no aliases
```

`SlowReaderBehaviorWireStrings.TryParse` and `PublisherWireStrings.TryParseDataset` exist too.

**Consequence: spec issue #2 is deleted, not implemented.** "Two additive changes to shipped
packages" becomes one. Nothing else in the spec changes — §4's claim that `Schema` and `SType` are
resolved rather than bound still holds, and the resolver calls these existing methods.

### C2 — Spec §8's flagged risk is resolved. The binding generator works in a library.

§8 says the generator "must be verified in a library rather than assumed" and names a hand-written
binder as the fallback. Verified, in a library project carrying `IsAotCompatible`,
`EnableTrimAnalyzer`, `EnableAotAnalyzer` and `TreatWarningsAsErrors`:

| `EnableConfigurationBindingGenerator` | Result |
|---|---|
| `true` | Build succeeded. 0 warnings, 0 errors. |
| `false` | 6 errors: IL2026 ×3 and IL3050 ×3 |

All three call shapes are covered by the generator: `OptionsBuilder<T>.Bind(IConfiguration)`,
`services.Configure<T>(name, section)`, and `OptionsBuilder<T>.BindConfiguration(path)`. Bound at
runtime as well as compiled — the exact §4 JSON shape, including the nested `Subscriptions` object
list and the `Symbols` string list, round-tripped correctly.

**Consequence: no fallback binder, and no spike task.** The property is set in Task 3 and a guard
test in the same task keeps it set.

### C3 — `DurationPattern.Roundtrip` does not parse `"PT30S"`. Use `PeriodPattern.NormalizingIso`.

§4 says ISO-8601 duration strings are "parsed by `DurationPattern.Roundtrip`". They are not.
`DurationPattern.Roundtrip` parses NodaTime's own `days:hh:mm:ss` form. Measured:

```
DurationPattern.Roundtrip     "PT30S"       -> FAIL  "does not match the required number from the format string \"D\""
DurationPattern.Roundtrip     "0:00:00:30"  -> OK    0:00:00:30
PeriodPattern.NormalizingIso  "PT30S"       -> OK    Period; .ToDuration() = 0:00:00:30
PeriodPattern.NormalizingIso  "PT1H30M"     -> OK    .ToDuration() = 0:01:30:00
```

`PeriodPattern.NormalizingIso.Parse(text).Value.ToDuration()` is the crossing, and it needs two
guards beyond parse success, both measured:

- `"P1M"` and `"P1Y"` **parse** and then throw `InvalidOperationException: Cannot construct duration
  of period with non-zero months or years.` A month is not a fixed length. Reject
  `Period.Months != 0 || Period.Years != 0` before calling `ToDuration()`, so the message names the
  configuration path rather than surfacing NodaTime's.
- `"PT-5S"` parses to a negative duration. A negative backoff is meaningless; reject it.

**Consequence:** §4's sentence changes and `LiveSessionResolver` uses `PeriodPattern`. The reasoning
§4 gives for choosing ISO-8601 is unaffected and still right.

### C4 — The runner cannot live in `Internal/`. This repo has no `InternalsVisibleTo`.

§3's layout puts `LiveSessionRunner`, `ReconnectSupervisor` and `ExtensionsLog` under `Internal/`,
while §5 and §7 rest on `LiveSessionRunnerTests` driving the runner directly over `MockLiveGateway`
— "no host and no container". Both cannot be true. CLAUDE.md: *"the repo declares no
`InternalsVisibleTo` anywhere and this is not worth being the first."*

**Resolution: the runner and everything a test or a non-host consumer needs is public**, at the
package root. That is not a concession — it is §5's own argument carried to its conclusion. The
package exists for the loop, and a loop that only a `BackgroundService` can reach is a loop a
console app or a non-Microsoft host cannot use.

Public: `ILiveRecordHandler`, `LiveSessionRunner`, `LiveSessionState`, `ResolvedLiveSession`,
`ResolvedReconnect`, `ResolvedSubscription`, `LiveSessionResolver`, `LiveSessionResolutionResult`,
`ReconnectSupervisor`, `LiveSessionService`, the options DTOs, the registration extensions, and
`LiveSessionMetrics`.

Internal: `ExtensionsLog` only — it is `[LoggerMessage]` partials, exactly as
`Historical/Internal/HistoricalLog.cs` is, and nothing outside the assembly calls it.

The cost is `PublicAPI.Unshipped.txt` entries, which is the cost the spec already accepted:
`PublicAPI.Shipped.txt` stays empty through 1.1.0.

---

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from
CLAUDE.md, `Directory.Build.props` and the spec.

- **`net10.0` only.** No `net11.0`, and **no conditional compilation anywhere** — the repo contains
  no `#if` and none is to be added.
- **`DatabentoDotNet.*` everywhere** — package id, assembly, namespace, project. **Never
  `Databento.*`.** The one deliberate exception is the registration extensions class, which sits in
  namespace `Microsoft.Extensions.DependencyInjection` so `Add*` appears on `IServiceCollection`
  with no extra `using`.
- **NodaTime only. The five BCL date/time types are banned as *types*** by `BannedSymbols.txt` +
  `Microsoft.CodeAnalysis.BannedApiAnalyzers`, reported as RS0030, promoted to an error by
  `TreatWarningsAsErrors`. `T:System.TimeSpan` included. This applies to test projects too. A
  `TimeSpan`-typed property, parameter, local or `var` whose inferred type is `TimeSpan` will not
  compile. `Duration.FromMinutes(5).ToTimeSpan()` is legal — RS0030 fires on the type being *named*
  in source, and that form never names it. `HistoricalClient.cs:1364` already relies on the same
  rule for `System.Threading.Timeout.InfiniteTimeSpan`.
- **`TreatWarningsAsErrors=true`**, `Nullable=enable`, `ImplicitUsings=enable`,
  `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`.
- **`$(ShippingProject)` is automatic.** `Directory.Build.targets:41` sets it for any project that
  is not marked `IsTestProject` / `IsBenchmarkProject` / `IsProbeProject` / `IsSampleProject`. It
  brings `IsAotCompatible`, `EnableTrimAnalyzer`, `EnableAotAnalyzer`, the
  `Microsoft.CodeAnalysis.PublicApiAnalyzers` reference, `LICENSE` packing, and `README.md` +
  `icon.png` packing. It also **fails the build** via the `EnsurePublicApiBaselineExists` target
  unless both `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` sit beside the project file.
- **`PublicAPI.Shipped.txt` stays empty. The whole baseline goes in `PublicAPI.Unshipped.txt`** —
  the arrangement `Directory.Build.targets:73-80` argues for and that #68 moves across at 1.0.0.
- **Central package management.** Versions live in `Directory.Packages.props`; project files carry
  `<PackageReference Include="..." />` with no `Version`. `nuget.config` pins restore to nuget.org
  with `<clear />`.
- **An issue exists before work starts.** Every commit references one: `Fixes #N` / `Refs #N`. Every
  issue carries a milestone (M0–M6) and one `type:` plus at least one `area:` label.
- **One canonical copy of each fact.** Behaviour changes and their guide land in the same commit.
- **Zero allocation per record** on the `FillBufferAsync` / `TryNextRecord` path. Asserted, not
  asserted-to.
- **The free/billable test split is by file.** No test in this package starts a live session against
  the real gateway, so CLAUDE.md's free/billable table gains no row.

### Verified facts this plan depends on

Each was established by running something during planning. Do not re-derive them; do not assume the
opposite.

| Fact | Evidence |
|---|---|
| `EnableConfigurationBindingGenerator=true` makes `Bind`/`Configure`/`BindConfiguration` clean in a library under both analyzers | build with it `true` → 0 warnings; `false` → IL2026 ×3, IL3050 ×3 |
| The §4 JSON binds correctly at runtime, nested object list and string list included | ran it: `Dataset=EQUS.MINI`, `Subs=1`, `Symbols=AAPL,MSFT`, `MaxAttempts=7` |
| `PeriodPattern.NormalizingIso` parses `"PT30S"`; `DurationPattern.Roundtrip` does not | see C3 |
| `Period.ToDuration()` throws on non-zero months or years; `"PT-5S"` yields a negative | see C3 |
| `ValidateOnStart()` fails `host.StartAsync()` with `OptionsValidationException` carrying `Failures` | ran it: `host boot failed: Dataset is required for 'bad'.` |
| Keyed singletons resolve by name (`AddKeyedSingleton` / `GetRequiredKeyedService`) | ran it |
| An interface method may take a `scoped RecordRef` parameter, and the pump loop compiles against the real `LiveClient` under `IsAotCompatible` | compiled `ILiveRecordHandler` + `Drain`/`PumpAsync` against `src/DatabentoDotNet.Live` → 0 warnings |
| `Meter` / `Counter<long>` need **no** `PackageReference` on net10.0 | compiled and ran a `Meter` + `MeterListener` with none |
| `HistoricalGateway` has exactly one member, `Bo1` | `HistoricalGateway.cs:26` |
| `ReferenceClient(HistoricalClient)` exists, copies five properties, and does **not** dispose the transport | `ReferenceClient.cs:147-171`, `:392-399` |

---

## File structure

Every file, what it is responsible for, and the task that creates it. Files that change together
live together; `Options/` groups the binding surface because it changes as one thing when a
configuration key is added.

```
src/DatabentoDotNet.Extensions.Hosting/
  DatabentoDotNet.Extensions.Hosting.csproj   project; four ProjectReferences; binder generator     T3
  PublicAPI.Shipped.txt                       empty, and stays empty through 1.1.0                  T3
  PublicAPI.Unshipped.txt                     the whole baseline; grows in almost every task         T3
  README.md                                   the package page on nuget.org               T3 stub / T13

  Options/DatabentoOptions.cs                 root: ApiKey                                          T4
  Options/HistoricalOptions.cs                ApiKey, BaseUrl, UserAgentExtension, pool lifetime    T4
  Options/LiveSessionOptions.cs               + SubscriptionOptions, ReconnectOptions               T4
  Options/ResolvedLiveSession.cs              + ResolvedReconnect — the real types                  T4
  Options/LiveSessionResolver.cs              the one live crossing, and its failure list           T4
  Options/HistoricalResolver.cs               the same, for the shared HTTP transport               T5
  Options/LiveSessionValidator.cs             IValidateOptions; calls the resolver, holds no rules  T5
  Options/HistoricalValidator.cs              the same, for HistoricalOptions                       T5

  ServiceCollectionExtensions.cs              AddDatabento / …Historical / …Reference / …Live    T5, T10
  DatabentoLiveBuilder.cs                     .AddRecordHandler<T>()                    T5, +T10 health

  ReconnectSupervisor.cs                      backoff, jitter, bounded consecutive attempts         T6
  ILiveRecordHandler.cs                       the dispatch contract                       T5 stub / T7
  LiveSessionState.cs                         NotStarted/Starting/Running/Reconnecting/Stopped/Faulted T7
  LiveSessionRunner.cs                        the entire loop — no host, no container    T7, T8, +T10
  Internal/ExtensionsLog.cs                   [LoggerMessage] partials, stable event ids            T7
  LiveSessionService.cs                       BackgroundService; thin by construction               T9
  LiveSessionMetrics.cs                       the Meter and its four instruments                   T10
  HealthChecks/LiveSessionHealthCheck.cs      Healthy / Degraded / Unhealthy from LiveSessionState T10

tests/DatabentoDotNet.Extensions.Hosting.Tests/
  DatabentoDotNet.Extensions.Hosting.Tests.csproj                                                   T3
  GlobalUsings.cs                                                                                   T3
  ConfigurationBindingTests.cs                the generator guard — see T3                          T3
  LiveSessionResolverTests.cs                 wire strings, durations, failure paths                T4
  OptionsValidationTests.cs                   ValidateOnStart, and the message naming its path      T5
  RegistrationTests.cs                        a real ServiceProvider; two named sessions            T5
  ReconnectSupervisorTests.cs                 the schedule, without a socket and without waiting    T6
  RecordingHandler.cs                         the ILiveRecordHandler test double                    T7
  LiveSessionRunnerTests.cs                   drain order, flush points, stream end, shutdown       T7
  LiveSessionReconnectTests.cs                order, backoff, MaxAttempts, the counter resetting    T8
  LiveSessionServiceTests.cs                  host boot, and a bad key failing it                   T9
  ObservabilityTests.cs                       health transitions and the four instruments          T10
  ExtensionsAllocationTests.cs                zero bytes per record, and the counter-test          T11

tools/DatabentoDotNet.AotProbe/
  HostedSessionProbe.cs                       a HostApplicationBuilder inside the native binary    T12

samples/DatabentoDotNet.Samples.HostedLive/
  DatabentoDotNet.Samples.HostedLive.csproj                                                        T13
  Program.cs                                                                                       T13
  appsettings.json                            the first sample with one, which is the point of it  T13

docs/guides/hosting-and-dependency-injection.md                                                    T13
```

Modified, not created:

| File | Change | Task |
|---|---|---|
| `ROADMAP.md` | new `## 8. Milestone 6`; renumber Sequencing → §9, Open questions → §10; answer question 5 for hosting | T1 |
| `docs/plans/m6-hosting-extensions.md` | fold in corrections C1–C4 | T1 |
| `src/DatabentoDotNet.Historical/HistoricalClient.cs` | `Handler` + `DisposesHandler`; `CreateHttpClient` | T2 |
| `src/DatabentoDotNet.Historical/PublicAPI.Unshipped.txt` | four entries | T2 |
| `Directory.Packages.props` | four `PackageVersion` entries, plus `Microsoft.Extensions.Hosting` for the probe | T3, T12 |
| `DatabentoDotNet.slnx` | two projects, then the sample | T3, T13 |
| `tools/DatabentoDotNet.AotProbe/*.csproj`, `Program.cs` | the fifth reference and the new probe | T12 |
| `docs/docfx.json` | the fifth project in `metadata.src.files` | T13 |
| `docs/guides/toc.yml`, `docs/release-notes.md`, `samples/README.md`, root `README.md` | the new guide, note, sample and package row | T13 |

**`docs/docfx.json` is the one row above that is a decision rather than bookkeeping.** Its
`metadata.src.files` names the four shipping projects one by one, and its own comment says why: *"A
glob would silently pick up a fifth project the day one is added, and whether a new project belongs
in the published reference is a decision somebody should make in a diff."* Task 13 is that diff, and
it makes the decision explicitly rather than by omission.

---

## Task order and dependencies

```
T1  setup: milestone, label, ROADMAP, spec corrections
     │
     ├──> T2  HttpMessageHandler seam on HistoricalClient        (M5 — ships in 1.0)
     │         │
     └──> T3  project skeleton + binder guard                    │
           │                                                     │
           └──> T4  options model + LiveSessionResolver          │
                 │                                               │
                 └──> T5  validation + registration  <───────────┘
                       │
                       ├──> T6  ReconnectSupervisor
                       │         │
                       └─────────┴──> T7  ILiveRecordHandler + LiveSessionRunner
                                            │
                                            └──> T8  recovery: reconnect, resubscribe, restart
                                                  │
                                                  └──> T9  LiveSessionService + wiring
                                                        │
                                                        └──> T10 metrics + health check
                                                              │
                                                              └──> T11 allocation assertion
                                                                    │
                                                                    └──> T12 AOT probe drives a host
                                                                          │
                                                                          └──> T13 guide, README, sample
```

**T2 is the only task that ships in 1.0**, is independent of T3–T4, and can be done in parallel with
them — but it **blocks T5**, which needs the seam to hand `IHttpMessageHandlerFactory`'s handler to
`HistoricalClient`.

**T6 goes before T7** because the backoff is pure arithmetic behind two injected seams, so it is
settled without a socket — and the runner that consumes it then has nothing left to prove about
scheduling. Taking them in the other order would mean writing the runner against a type that does
not exist yet, or writing the backoff inline and extracting it afterwards.

**T13 lands last**, because CLAUDE.md requires a behaviour change and its guide in one commit and the
behaviour is not settled until T12 passes.

---

## Task 1: Repository setup — milestone, label, ROADMAP, spec corrections

**Files:**
- Modify: `ROADMAP.md` (new `## 8`, renumber the two sections after it)
- Modify: `docs/plans/m6-hosting-extensions.md` (corrections C1–C4)

**Interfaces:**
- Consumes: nothing.
- Produces: the `M6: Hosting extensions` milestone and the `area: extensions` label, which every
  later task's issue references; a spec that no longer contradicts this plan.

No code, and therefore no test cycle — the deliverable is checkable by reading. It is a separate
task because every later task's issue cannot be filed without the milestone and the label, and
because CLAUDE.md makes both mandatory rather than optional.

- [ ] **Step 1: Confirm the milestone and label do not already exist**

```bash
gh milestone list 2>/dev/null || gh api repos/jerbersoft/databentodotnet/milestones --jq '.[].title'
gh label list --search extensions
```

Expected: no `M6: Hosting extensions`, no `area: extensions`.

- [ ] **Step 2: Create the milestone and the label**

```bash
gh api repos/jerbersoft/databentodotnet/milestones \
  -f title='M6: Hosting extensions' \
  -f description='DatabentoDotNet.Extensions.Hosting — DI registration, IConfiguration binding, a hosted live session, health checks and metrics. Ships as 1.1.0, after 1.0.'

gh label create 'area: extensions' \
  --description 'Hosting extensions — DI registration, options binding, the hosted live session' \
  --color 'BFD4F2'
```

- [ ] **Step 3: Read the two ROADMAP sections that are about to move**

Run: `grep -n '^## ' ROADMAP.md`

Expected: `## 7. Milestone 5 — Polish & release`, `## 8. Sequencing`, `## 9. Open questions`. The new
M6 section becomes `## 8` and the two below it become `## 9` and `## 10`.

- [ ] **Step 4: Insert the M6 section into ROADMAP.md before `## 8. Sequencing`**

Section heading `## 8. Milestone 6 — Hosting extensions (post-1.0)`. Its body states, each in a
sentence or two, and each already argued in the spec rather than restated at length:

1. What ships — the five files' worth of capability in `§3` of the spec, and the package id.
2. **It ships as 1.1.0, not in 1.0**, and why: five packages locked together would give the
   extensions surface a SemVer promise on its first day, which is what #68 refused to do for the
   core four after a full milestone.
3. The one core change that goes into **1.0** because it is core surface: the `HttpMessageHandler`
   seam on `HistoricalClient`.
4. That designing this package is what found that gap, before a line of it was written — the
   evidence #68 said it was waiting for, arriving in the form #68 described.
5. That `ReferenceClient` needs no change, because `ReferenceClient(HistoricalClient)` was written
   for this consumer before this consumer existed.

Then renumber: `## 8. Sequencing` → `## 9. Sequencing`, `## 9. Open questions` → `## 10. Open
questions`. Extend the sequencing diagram's last line to `─> M5 Polish ──> 1.0 ──> M6 Hosting ──> 1.1`.

- [ ] **Step 5: Answer ROADMAP open question 5 for the hosting case**

Question 5 reads `**API-key handling** — env var (\`DATABENTO_API_KEY\`) by default, matching the
other clients?` Append to it, leaving the question open for the core clients where it still is:

> **Partially resolved for hosting (M6):** `DatabentoDotNet.Extensions.Hosting` reads
> `DATABENTO_API_KEY` as the last step of its precedence chain — session options, then root options,
> then the variable. It is a second mechanism alongside the configuration provider's own
> `Databento__ApiKey`, which is a real cost; it is paid because a reader who has run any Databento
> sample has that variable set and will expect it to work. The four clients themselves still take an
> `ApiKey` and read no environment, which is what keeps this question open for them.

- [ ] **Step 6: Fold corrections C1–C4 into the spec**

In `docs/plans/m6-hosting-extensions.md`:

1. **Delete §2b entirely** and retitle §2 to *"One additive change to a shipped package, in 1.0"*.
   Replace the deleted subsection with three sentences recording that it was proposed, that
   `WireStrings.TryParseSchema` / `TryParseSType` were found to exist already — public, in the
   baseline, and round-tripped by `EnumWireStringTests.cs:309-313` — and that the resolver calls
   them. **Keep the "one canonical copy" argument against a parser inside the extensions package**
   in §9; it is now the reason to *call* `WireStrings` rather than the reason to extend it.
2. Delete the issue #2 row from §10's table and renumber the rows below it. Change "Issues 1 and 2
   are 1.0 blockers" to "Issue 1 is a 1.0 blocker".
3. In §4, replace `DurationPattern.Roundtrip` with `PeriodPattern.NormalizingIso` and add the two
   guards from C3 (months/years, and negative).
4. In §8, replace the paragraph flagging the binding generator as unverified with the measured
   result from C2, and delete the sentence naming it "the implementation plan's first spike".
5. In §3, move `LiveSessionRunner.cs` and `ReconnectSupervisor.cs` out of `Internal/` to the package
   root, leaving only `ExtensionsLog.cs` there, and add the C4 paragraph explaining that a runner a
   test cannot reach contradicts §5.

- [ ] **Step 7: Verify the two documents no longer disagree**

```bash
grep -n 'TryFromWireString\|DurationPattern\|first spike\|Internal/LiveSessionRunner' docs/plans/m6-hosting-extensions.md
grep -n '^## ' ROADMAP.md | tail -4
```

Expected: the first command prints nothing. The second prints `## 8. Milestone 6 — Hosting
extensions (post-1.0)`, `## 9. Sequencing`, `## 10. Open questions`.

- [ ] **Step 8: File the eight issues**

One per remaining task, each with its milestone and labels, each BLUF-first with a definition of
done specific enough to disagree with. Use `.github/ISSUE_TEMPLATE/`'s task shape: **BLUF → Scope →
Definition of done → References → Porting notes.**

| Tasks | Title | Milestone | Labels | Referenced in commits as |
|---|---|---|---|---|
| T2 | `HttpMessageHandler` seam on `HistoricalClient` | M5 | `type: feature`, `area: historical` | `#<seam>` |
| T3–T5 | Project, options model, resolver, validation, registration | M6 | `type: feature`, `area: extensions`, `area: build` | `#<registration>` |
| T6, T8 | Reconnect supervisor and its backoff | M6 | `type: feature`, `area: extensions`, `area: live` | `#<reconnect>` |
| T7, T9 | `ILiveRecordHandler`, `LiveSessionRunner`, the hosted service | M6 | `type: feature`, `area: extensions`, `area: live` | `#<runner>` |
| T10 | Health checks and metrics | M6 | `type: feature`, `area: extensions` | `#<observability>` |
| T11 | Zero-allocation assertion through the runner | M6 | `type: test`, `area: extensions` | `#<allocation>` |
| T12 | Extend the AOT probe to drive a host | M6 | `type: chore`, `area: extensions`, `area: build` | `#<aot>` |
| T13 | Guide, package README, and the `HostedLive` sample | M6 | `type: docs`, `area: extensions` | `#<docs>` |

**Two issues span two tasks each, and the pairing is deliberate.** The supervisor (T6) and the
recovery loop that uses it (T8) are one behaviour split across two test cycles; so are the runner
(T7) and the hosted service that drives it (T9). Splitting them into four issues would produce two
whose definition of done is "half of a thing works".

- [ ] **Step 9: Commit**

```bash
git checkout -b m6-hosting-extensions
git add ROADMAP.md docs/plans/m6-hosting-extensions.md docs/plans/m6-hosting-extensions-plan.md
git commit -m "docs: plan M6 hosting extensions, and correct four claims in its spec

Four claims in the design spec were checked by running something rather than
by reading, and four were wrong or unverified:

  - WireStrings.TryParseSchema/TryParseSType already exist, are public, are in
    the baseline, and are round-tripped by EnumWireStringTests. Spec issue #2
    is deleted rather than implemented.
  - EnableConfigurationBindingGenerator works in a library under both AOT
    analyzers and TreatWarningsAsErrors; without it the same code is six
    errors. The fallback binder is unnecessary.
  - DurationPattern.Roundtrip does not parse \"PT30S\". PeriodPattern.NormalizingIso
    does, with two guards: non-zero months or years, and negatives.
  - A runner under Internal/ contradicts the spec's own testing section, since
    this repo declares no InternalsVisibleTo. It is public.

Refs #0"
```

---

## Task 2: `HttpMessageHandler` seam on `HistoricalClient` (M5 — ships in **1.0**)

**Files:**
- Modify: `src/DatabentoDotNet.Historical/HistoricalClient.cs` — new properties beside
  `UserAgentExtension` (`:237`); `CreateHttpClient()` at `:1327`
- Modify: `src/DatabentoDotNet.Historical/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Historical.Tests/HistoricalClientHandlerTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  public HttpMessageHandler? Handler { get; init; }        // null => HttpClient builds its own
  public bool DisposesHandler { get; init; } = true;       // matches HttpClient's own default
  ```
  Task 5 sets `Handler = factory.CreateHandler("Databento")` and `DisposesHandler = false`.

**Why it is a `HttpMessageHandler` and not an `HttpClient`.** A full-client seam makes
`AddHttpClient<HistoricalClient>()` work in the textbook way and loses the property that the API key
reaches the wire from exactly one place: either the client mutates an object it does not own to
attach `Authorization`, or the caller attaches it and the key has two paths to the wire. Spec §9.

**The motivating defect, so the test asserts the right thing.** `new HttpClient()` uses
`SocketsHttpHandler` with `PooledConnectionLifetime` defaulted to infinite, so a singleton in a host
that stays up for weeks keeps talking to whatever IP `hist.databento.com` resolved to on the first
request. `IHttpClientFactory` exists to fix that and no part of the current surface can reach it.

**`DisposeAsync` needs no change.** `new HttpClient(handler, disposeHandler)` already honours the
flag, so `_http.Value.Dispose()` at `:1025` does the right thing in both cases without knowing about
either property. Do not add a branch there.

- [ ] **Step 1: Write the failing tests**

Create `tests/DatabentoDotNet.Historical.Tests/HistoricalClientHandlerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The <see cref="HistoricalClient.Handler"/> seam: a caller may supply the
/// <see cref="HttpMessageHandler"/> the client sends through, which is how
/// <c>IHttpClientFactory</c> — and therefore a bounded <c>PooledConnectionLifetime</c> — becomes
/// reachable from a long-running host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential assertion is the load-bearing one here.</b> Everything the client does to a
/// request it builds — HTTP Basic from the <see cref="ApiKey"/>, the validated
/// <c>User-Agent</c>, the <c>Accept</c> header, the base address — has to survive a supplied
/// handler, or the seam has quietly become a second path to the wire. That is the property
/// <see cref="HistoricalClient.ApiKey"/>'s remarks promise and the reason a full
/// <see cref="HttpClient"/> seam was rejected.
/// </para>
/// <para>
/// A recording handler rather than <see cref="MockHistoricalGateway"/>, and deliberately: what is
/// under test is which handler the client sends <em>through</em>, which a real socket cannot
/// report. <see cref="HistoricalClientTests"/> keeps the socket-level coverage.
/// </para>
/// </remarks>
public class HistoricalClientHandlerTests
{
    private const string ApiKeyValue = "test-API________________________";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendAsync_WithASuppliedHandler_SendsThroughIt()
    {
        using var handler = new RecordingHandler();
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SendAsync_WithASuppliedHandler_StillSendsEveryHeaderTheClientOwns()
    {
        using var handler = new RecordingHandler();
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
            UserAgentExtension = "MyApp/1.0",
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), Cancel);

        var request = Assert.Single(handler.Requests);

        // HTTP Basic with the key as the username and an empty password — the one place in this
        // library where the key reaches the wire, asserted here because the seam is exactly where
        // a second place would appear.
        var authorization = request.Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Basic", authorization.Scheme);
        Assert.Equal(
            ApiKeyValue + ":",
            Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter!)));

        Assert.Contains("MyApp/1.0", request.Headers.UserAgent.ToString());
        Assert.Contains(
            request.Headers.Accept,
            media => media.MediaType == HistoricalClient.JsonMediaType);

        // The base address still resolved, so the seam did not cost the gateway either.
        Assert.Equal("hist.databento.com", request.RequestUri!.Host);
        Assert.Equal("/v0/metadata.list_datasets", request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task DisposeAsync_WithDisposesHandlerFalse_LeavesTheHandlerUsable()
    {
        using var handler = new RecordingHandler();
        var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
            DisposesHandler = false,
        };

        (await client.GetPathAsync(HistoricalClient.PathFor("metadata.list_datasets"), Cancel))
            .Dispose();
        await client.DisposeAsync();

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_ByDefault_DisposesTheSuppliedHandler()
    {
        // The default is true because HttpClient's own is, and a caller who supplies a handler and
        // says nothing has handed over its lifetime. IHttpClientFactory's caller says otherwise —
        // which is what the property is for.
        var handler = new RecordingHandler();
        var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ApiKeyValue),
            Handler = handler,
        };

        (await client.GetPathAsync(HistoricalClient.PathFor("metadata.list_datasets"), Cancel))
            .Dispose();
        await client.DisposeAsync();

        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task SendAsync_WithNoHandler_StillWorks()
    {
        // The property is additive: a client that sets neither behaves exactly as it did before.
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get("metadata.list_datasets", MockHistoricalResponse.Json("""[{"dataset":"GLBX.MDP3"}]"""));

        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
            BaseUrl = gateway.BaseUrl,
        };

        using var response = await client.GetPathAsync(
            HistoricalClient.PathFor("metadata.list_datasets"), Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Answers every request with an empty JSON array and keeps what it was asked, so a test can
    /// assert which handler the client sent through and what it put on the request.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public int Count => _requests.Count;

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, HistoricalClient.JsonMediaType),
                RequestMessage = request,
            };

            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Historical.Tests --filter "FullyQualifiedName~HistoricalClientHandlerTests"`

Expected: FAIL — compile errors, `'HistoricalClient' does not contain a definition for 'Handler'`
and the same for `DisposesHandler`.

- [ ] **Step 3: Add the two properties**

In `src/DatabentoDotNet.Historical/HistoricalClient.cs`, immediately after `UserAgentExtension`
(which ends at `:237`) and before `LoggerFactory`:

```csharp
    /// <summary>
    /// The <see cref="HttpMessageHandler"/> to send through, or <see langword="null"/> to let
    /// <see cref="HttpClient"/> build its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists for <c>IHttpClientFactory</c>, and the defect it fixes is not socket
    /// exhaustion.</b> <see cref="HttpClient"/>'s own handler is a
    /// <see cref="System.Net.Http.SocketsHttpHandler"/> whose <c>PooledConnectionLifetime</c>
    /// defaults to infinite, so a client held as a singleton in a host that stays up for weeks
    /// keeps talking to whatever address <c>hist.databento.com</c> resolved to on its first
    /// request. A handler supplied here can bound that; nothing else on this type can.
    /// </para>
    /// <para>
    /// <b>A handler, not an <see cref="HttpClient"/>, and that is the whole design.</b> Everything
    /// this client puts on a request it builds — HTTP Basic from the <see cref="ApiKey"/>, the
    /// validated <c>User-Agent</c>, the <c>Accept</c> header, the base address — is still built
    /// here and still built once. Handing over the whole client would mean either mutating an
    /// object this type does not own to attach the <c>Authorization</c> header, or letting the
    /// caller attach it — and then the key has two paths to the wire, which is exactly what
    /// <see cref="ApiKey"/>'s redacted <see cref="object.ToString"/> and the single-header rule
    /// exist to prevent.
    /// </para>
    /// </remarks>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>
    /// Whether <see cref="DisposeAsync"/> disposes <see cref="Handler"/>. Defaults to
    /// <see langword="true"/>, as <see cref="HttpClient"/>'s own parameter does.
    /// </summary>
    /// <remarks>
    /// Set it to <see langword="false"/> when the handler's lifetime belongs to somebody else —
    /// which is the <c>IHttpMessageHandlerFactory</c> case, where the factory pools handlers
    /// across clients and rotates them on its own schedule. Disposing one out from under it would
    /// break every other client sharing it. Ignored when <see cref="Handler"/> is
    /// <see langword="null"/>: a handler this client built is a handler this client disposes.
    /// </remarks>
    public bool DisposesHandler { get; init; } = true;
```

- [ ] **Step 4: Use the handler in `CreateHttpClient()`**

Replace the two-line opening of `CreateHttpClient()` at `:1327-1333`:

```csharp
    private HttpClient CreateHttpClient()
    {
        // No handler of our own unless the caller supplied one. HttpClient's automatic
        // decompression is off by default and would be irrelevant if it were not: the zstd frame
        // the API returns is in the body and nothing announces it in Content-Encoding, so
        // ReadZstdJsonLinesAsync unwraps it itself.
        //
        // DisposeAsync needs no branch for this. HttpClient's own disposeHandler parameter already
        // decides whether Dispose reaches the handler, so the one call at the end of DisposeAsync
        // does the right thing in both cases without knowing either property exists.
        var http = Handler is null
            ? new HttpClient()
            : new HttpClient(Handler, DisposesHandler);

        http.BaseAddress = EffectiveBaseUrl();
```

Everything below that line — the `Authorization` header, the validated `User-Agent`, `Accept`, and
the infinite `Timeout` — is unchanged. Do not touch it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Historical.Tests --filter "FullyQualifiedName~HistoricalClientHandlerTests"`

Expected: PASS, 5 tests.

- [ ] **Step 6: Update the public API baseline**

The build fails with RS0016 until the two entries exist. Add them to
`src/DatabentoDotNet.Historical/PublicAPI.Unshipped.txt`, in the file's existing sort order:

```
DatabentoDotNet.Historical.HistoricalClient.DisposesHandler.get -> bool
DatabentoDotNet.Historical.HistoricalClient.DisposesHandler.init -> void
DatabentoDotNet.Historical.HistoricalClient.Handler.get -> System.Net.Http.HttpMessageHandler?
DatabentoDotNet.Historical.HistoricalClient.Handler.init -> void
```

- [ ] **Step 7: Run the whole suite**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS, and no RS0016/RS0017.

- [ ] **Step 8: Commit**

```bash
git add src/DatabentoDotNet.Historical/HistoricalClient.cs \
        src/DatabentoDotNet.Historical/PublicAPI.Unshipped.txt \
        tests/DatabentoDotNet.Historical.Tests/HistoricalClientHandlerTests.cs
git commit -m "feat(historical): let a caller supply the HttpMessageHandler

A singleton HistoricalClient in a long-running host keeps talking to whatever
IP hist.databento.com resolved to on its first request: HttpClient's own
SocketsHttpHandler defaults PooledConnectionLifetime to infinite, and nothing
on the surface could reach IHttpClientFactory to bound it.

A handler rather than a whole HttpClient, so the API key still reaches the wire
from exactly one place. DisposeAsync needs no change — HttpClient's own
disposeHandler parameter already decides whether Dispose reaches the handler.

Fixes #<seam>"
```

---

## Task 3: Project skeleton, and the guard that keeps the binder generated

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj`
- Create: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Shipped.txt` (empty)
- Create: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt` (empty)
- Create: `src/DatabentoDotNet.Extensions.Hosting/README.md` (a placeholder; T12 writes it)
- Create: `tests/DatabentoDotNet.Extensions.Hosting.Tests/DatabentoDotNet.Extensions.Hosting.Tests.csproj`
- Create: `tests/DatabentoDotNet.Extensions.Hosting.Tests/GlobalUsings.cs`
- Create: `tests/DatabentoDotNet.Extensions.Hosting.Tests/ConfigurationBindingTests.cs`
- Modify: `Directory.Packages.props`
- Modify: `DatabentoDotNet.slnx`

**Interfaces:**
- Consumes: nothing.
- Produces: two buildable projects, and the guarantee that `EnableConfigurationBindingGenerator` is
  on — which every later task's options code depends on and which fails as six IL2026/IL3050 errors
  if it is ever turned off.

- [ ] **Step 1: Add the four package versions**

In `Directory.Packages.props`, inside the first unlabelled `<ItemGroup>` (the one carrying
`NodaTime` and `Microsoft.Extensions.Logging.Abstractions`), after the logging entry:

```xml
    <!--
      The four packages DatabentoDotNet.Extensions.Hosting adds, and they reach consumers of that
      package only — the core four keep the eight-package closure #71 and #74 verified.

      Abstractions where an abstraction exists. Hosting.Abstractions carries BackgroundService and
      is what a consumer's own host already implements; taking Microsoft.Extensions.Hosting itself
      would make this package bring a host rather than plug into one.

      The health check package is the one exception, and it is a measured trade rather than an
      oversight. IHealthCheck, HealthCheckResult and HealthCheckRegistration are all in the
      Abstractions half — but IHealthChecksBuilder and HealthCheckServiceOptions, which are what a
      registration has to reach to *install* a check, are only in the implementation half. Checked
      by listing both assemblies. So .AddHealthCheck() on DatabentoLiveBuilder either takes this
      dependency or does not exist, and a health check a consumer has to wire up themselves is not
      an opt-in feature, it is documentation. Any app that would call .AddHealthCheck() already has
      the package, because ASP.NET Core's own AddHealthChecks() comes from it; what this costs is
      one extra transitive package for consumers who never ask for a check.

      Microsoft.Extensions.Http is the one that is not an abstraction package, and it is the point
      of the dependency rather than an oversight: IHttpMessageHandlerFactory and the pooling
      behind it are the implementation, which is exactly what #<seam>'s seam exists to reach.

      Microsoft.Extensions.Options.ConfigurationExtensions brings Options, Configuration.Abstractions
      and Configuration.Binder with it, so the binding surface needs no further entry.

      System.Diagnostics.Metrics needs no entry at all: Meter and Counter<long> are in the net10.0
      shared framework. Verified by compiling and running a Meter with no PackageReference.
    -->
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.11" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.11" />
```

- [ ] **Step 2: Create the project file**

`src/DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>$(LibraryTargetFrameworks)</TargetFrameworks>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <!--
      DatabentoDotNet.* throughout — package ID, assembly, and namespace. Never Databento.*:
      that is the vendor's namespace and an unreserved NuGet prefix they could claim at any
      time. See CLAUDE.md, "Naming".

      "Extensions.Hosting" rather than "Extensions" or "DependencyInjection", because a hosted
      live session is the capability that justifies a package. DI registration alone would not:
      three AddSingleton calls are not worth a fifth package, a fifth README, a fifth baseline
      and a fifth entry in every release note.
    -->
    <PackageId>DatabentoDotNet.Extensions.Hosting</PackageId>
    <AssemblyName>DatabentoDotNet.Extensions.Hosting</AssemblyName>
    <RootNamespace>DatabentoDotNet.Extensions.Hosting</RootNamespace>
    <Description>Databento for ASP.NET Core and the .NET generic host: IServiceCollection registration for the historical, reference and live clients, IConfiguration binding, and a hosted live-streaming service with bounded reconnection, health checks and metrics.</Description>
  </PropertyGroup>

  <!--
    The configuration binding source generator, and it is load-bearing rather than an optimisation.

    ConfigurationBinder.Bind, OptionsBuilder<T>.Bind(IConfiguration), BindConfiguration and the
    reflection-based Configure<T>(IConfiguration) are all annotated RequiresUnreferencedCode and
    RequiresDynamicCode. $(ShippingProject) turns on EnableTrimAnalyzer and EnableAotAnalyzer, and
    TreatWarningsAsErrors turns each of those annotations into IL2026 and IL3050 — six errors from
    three call sites. The generator intercepts them with generated code and the build is clean.

    Measured both ways in this exact configuration before the property was written down: true is 0
    warnings and 0 errors, false is 6 errors. ConfigurationBindingTests in the test project is the
    guard that keeps it that way, and it fails as a *build* error rather than as an assertion,
    which is why it is a compiled file rather than a runtime check.
  -->
  <PropertyGroup>
    <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
  </PropertyGroup>

  <!--
    NodaTime, because every duration and instant in the options model crosses into one. The BCL
    date/time types are banned repo-wide (CLAUDE.md, "Dates and times"), and T:System.TimeSpan is
    banned as a *type* — so an options DTO cannot carry a TimeSpan property at all, and the wire
    form is an ISO-8601 string parsed by PeriodPattern.NormalizingIso.
  -->
  <ItemGroup>
    <PackageReference Include="NodaTime" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Http" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="all" />
  </ItemGroup>

  <!--
    All four, and that is the one-package decision stated in the project file rather than only in
    the spec: installing this to register HistoricalClient in a web API also pulls Live, Reference
    and Dbn. Splitting into Extensions.Http and Extensions.Hosting was rejected — two baselines,
    two READMEs and two guides, to save a transitive reference to packages that are small, pure
    managed and AOT-clean.
  -->
  <ItemGroup>
    <ProjectReference Include="../DatabentoDotNet.Dbn/DatabentoDotNet.Dbn.csproj" />
    <ProjectReference Include="../DatabentoDotNet.Live/DatabentoDotNet.Live.csproj" />
    <ProjectReference Include="../DatabentoDotNet.Historical/DatabentoDotNet.Historical.csproj" />
    <ProjectReference Include="../DatabentoDotNet.Reference/DatabentoDotNet.Reference.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create the two baseline files and a placeholder README**

```bash
mkdir -p src/DatabentoDotNet.Extensions.Hosting
: > src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Shipped.txt
: > src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt
printf '# DatabentoDotNet.Extensions.Hosting\n\nDatabento for ASP.NET Core and the .NET generic host.\n' \
  > src/DatabentoDotNet.Extensions.Hosting/README.md
```

Both text files must exist even when empty, or the `EnsurePublicApiBaselineExists` target in
`Directory.Build.targets:101` fails the build with a message saying so. `PublicAPI.Shipped.txt`
stays empty through 1.1.0.

- [ ] **Step 4: Create the test project**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/DatabentoDotNet.Extensions.Hosting.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>$(LibraryTargetFrameworks)</TargetFrameworks>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
    <!-- MockLiveGateway, reached below, reinterprets record structs the same way the codec does. -->
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <!-- The mock gateway compresses; the library only ever decompresses. See the Live test project. -->
    <PackageReference Include="ZstdSharp.Port" />
    <!--
      A real host, not just its abstractions: LiveSessionServiceTests boots one and asserts that a
      bad key fails the boot, and ValidateOnStart's failure is raised by Host.StartAsync through
      IStartupValidator. Test-only, so nothing reaches a consumer.
    -->
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" />
  </ItemGroup>

  <!--
    MockLiveGateway by project reference rather than by <Compile Link>, which is the opposite of
    what tools/DatabentoDotNet.AotProbe does and is right for the opposite reason. The probe cannot
    take this reference: it would drag xunit and Microsoft.NET.Test.Sdk into a Native AOT publish,
    neither of which is trim-safe. This is already a test project, so those are already here, and a
    reference costs nothing a link would save. benchmarks/DatabentoDotNet.Benchmarks reaches the
    same file the same way.

    xunit discovers tests per assembly from each test project's own run, so the Live tests are
    found once, from their own project, and not a second time from this one.
  -->
  <ItemGroup>
    <ProjectReference Include="../../src/DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj" />
    <ProjectReference Include="../DatabentoDotNet.Live.Tests/DatabentoDotNet.Live.Tests.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Create `GlobalUsings.cs`**

```csharp
global using Xunit;
```

- [ ] **Step 6: Add both projects to the solution**

In `DatabentoDotNet.slnx`, add to the `/src/` folder after the Reference entry:

```xml
    <Project Path="src/DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj" />
```

and to the `/tests/` folder after the Live tests entry:

```xml
    <Project Path="tests/DatabentoDotNet.Extensions.Hosting.Tests/DatabentoDotNet.Extensions.Hosting.Tests.csproj" />
```

- [ ] **Step 7: Write the generator guard test**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/ConfigurationBindingTests.cs`. This file's real
assertion is that it **compiles** — if `EnableConfigurationBindingGenerator` is ever turned off in
the library, the three calls it exercises become IL2026 and IL3050 there and the build stops. The
runtime assertions are the second half: that the generated binder handles the shapes §4's JSON
actually uses.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The configuration binding source generator does the binding, and this is the guard that keeps
/// it doing so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The load-bearing assertion here is that the library compiles at all</b>, and it is not made
/// in this file — it is made by <c>DatabentoDotNet.Extensions.Hosting</c> building.
/// <c>ConfigurationBinder.Bind</c>, <c>OptionsBuilder&lt;T&gt;.Bind</c>,
/// <c>BindConfiguration</c> and the reflection-based <c>Configure&lt;T&gt;</c> are annotated
/// <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>;
/// <c>$(ShippingProject)</c> turns on both analyzers and <c>TreatWarningsAsErrors</c> turns each
/// annotation into an error. Measured: with the generator on, 0 warnings; with it off, six errors
/// from three call sites.
/// </para>
/// <para>
/// What this file adds is the runtime half. A generator that compiles but binds the wrong shape
/// would pass the build and fail in a consumer's <c>appsettings.json</c>, so the cases below are
/// exactly the shapes §4 of the design uses: a nested object, a list of nested objects, and a list
/// of strings.
/// </para>
/// </remarks>
public class ConfigurationBindingTests
{
    private static IConfiguration Configuration(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        try
        {
            return new ConfigurationBuilder().AddJsonFile(path).Build();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Bind_OverTheDesignedShape_FillsEveryLevel()
    {
        var configuration = Configuration(
            """
            {
              "Databento": {
                "ApiKey": "db-0000000000000000000000000000",
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
            """);

        var services = new ServiceCollection();
        services.AddOptions<LiveSessionOptions>("equities")
                .Bind(configuration.GetSection("Databento:Live:equities"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities");

        Assert.Equal("EQUS.MINI", options.Dataset);

        var subscription = Assert.Single(options.Subscriptions);
        Assert.Equal("mbp-1", subscription.Schema);
        Assert.Equal("raw_symbol", subscription.StypeIn);
        Assert.Equal(["AAPL", "MSFT"], subscription.Symbols);

        Assert.True(options.Reconnect.Enabled);
        Assert.Equal("PT1S", options.Reconnect.InitialDelay);
        Assert.Equal("PT30S", options.Reconnect.MaxDelay);
        Assert.Equal(10, options.Reconnect.MaxAttempts);
    }

    [Fact]
    public void Bind_OverAnAbsentSection_LeavesTheDefaults()
    {
        // A session declared in code with no configuration at all is a legal state — the lambda
        // overload may be supplying everything. The binder must not null out the defaults.
        var services = new ServiceCollection();
        services.AddOptions<LiveSessionOptions>("equities")
                .Bind(Configuration("{}").GetSection("Databento:Live:equities"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities");

        Assert.Null(options.Dataset);
        Assert.Empty(options.Subscriptions);
        Assert.True(options.Reconnect.Enabled);
        Assert.Equal("PT1S", options.Reconnect.InitialDelay);
    }
}
```

- [ ] **Step 8: Run the tests to verify they fail**

Run: `dotnet build`

Expected: FAIL — `LiveSessionOptions` does not exist yet. That is the correct failure; Task 4
creates it. Nothing in this task can pass until then, which is why the two tasks share one issue.

- [ ] **Step 9: Confirm the skeleton itself builds**

Temporarily exclude the test file and confirm both projects compile and the API-baseline target is
satisfied:

```bash
mv tests/DatabentoDotNet.Extensions.Hosting.Tests/ConfigurationBindingTests.cs /tmp/
dotnet build
mv /tmp/ConfigurationBindingTests.cs tests/DatabentoDotNet.Extensions.Hosting.Tests/
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. If instead it reports
`DatabentoDotNet.Extensions.Hosting is a shipping project, so its public API is locked by RS0016`,
Step 3 did not create both files.

- [ ] **Step 10: Commit**

```bash
git add Directory.Packages.props DatabentoDotNet.slnx \
        src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "chore(extensions): add the DatabentoDotNet.Extensions.Hosting projects

Fifth package, and a test project for it. Four new PackageVersion entries, all
of which reach consumers of this package only — the core four keep the
eight-package closure #71 and #74 verified.

EnableConfigurationBindingGenerator is on and is load-bearing: without it the
three binding call shapes are six IL2026/IL3050 errors under \$(ShippingProject)'s
analyzers and TreatWarningsAsErrors. Measured both ways.

Refs #<registration>"
```

---

## Task 4: The options model and the one conversion path

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/DatabentoOptions.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/HistoricalOptions.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/LiveSessionOptions.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/ResolvedLiveSession.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/LiveSessionResolver.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionResolverTests.cs`

**Interfaces:**
- Consumes: `WireStrings.TryParseSchema`, `WireStrings.TryParseSType`,
  `WireStrings.TryParseCompression`, `SlowReaderBehaviorWireStrings.TryParse`, `ApiKey(string)`,
  `Symbols.From(IEnumerable<string>)`, `Symbols.All`, `Subscription`, `PeriodPattern.NormalizingIso`,
  `InstantPattern.ExtendedIso`.
- Produces, and Tasks 5–11 all rely on these exact names:
  ```csharp
  public sealed class DatabentoOptions   { public const string DefaultSectionName = "Databento";
                                           public string? ApiKey { get; set; } }
  public sealed class HistoricalOptions  { … }
  public sealed class LiveSessionOptions { … }
  public sealed class SubscriptionOptions{ … }
  public sealed class ReconnectOptions   { … }

  public sealed record ResolvedLiveSession { … }
  public sealed record ResolvedReconnect   { … }

  public static class LiveSessionResolver
  {
      public const string ApiKeyEnvironmentVariable = "DATABENTO_API_KEY";
      public static string PathFor(string name);                       // "Databento:Live:{name}"
      public static LiveSessionResolutionResult Resolve(
          string name, LiveSessionOptions options, DatabentoOptions root, string? environmentApiKey);
  }

  public sealed class LiveSessionResolutionResult
  {
      public ResolvedLiveSession? Session { get; }
      public ImmutableArray<string> Failures { get; }
      [MemberNotNullWhen(true, nameof(Session))] public bool Succeeded { get; }
  }
  ```

**Two rules this task exists to enforce, and neither is negotiable.**

1. **There is exactly one crossing.** `LiveSessionValidator` (Task 5) and `LiveSessionRunner`
   (Task 6) both go through `Resolve`, so a configuration that validates is a configuration that
   resolves. This is the rule `DbnTime` already enforces for the `UndefTimestamp` sentinel — *"do
   not add a second conversion path that skips the check"* — applied to a different boundary for
   the same reason.
2. **The resolver never re-implements a check the library already makes.** It calls
   `new ApiKey(text)` and `Symbols.From(list)` and **catches `ArgumentException` to prefix the
   configuration path onto the library's own message**. A resolver that decided for itself what a
   valid key looks like would be a second copy of that rule, and the copy that silently disagrees is
   the one nobody is looking at.

   The corollary, stated so nobody adds it later: **`Subscription`'s two cross-property rules are
   not checked here.** `Subscription.Validate` is `internal` and runs inside
   `LiveClient.SubscribeAsync`, which Task 7 calls during host startup — so a snapshot combined with
   a replay start, or a snapshot on a schema other than `mbo`, still fails the boot, with
   `Subscription.Validate`'s own message. It just does not carry the configuration path. That is the
   right trade: one copy of the rule, and a message that says exactly what is wrong.

- [ ] **Step 1: Write the failing resolver tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionResolverTests.cs`:

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// <see cref="LiveSessionResolver"/> — the one crossing from bindable primitives to the library's
/// real types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure names its configuration path</b>, because the reader of the message is looking
/// at an <c>appsettings.json</c> and not at this assembly. A message that says only
/// <c>'mbp1' is not a Databento schema</c> leaves them searching a file for which of four
/// subscriptions it meant.
/// </para>
/// <para>
/// <b>The environment variable is a parameter, not an ambient read.</b>
/// <see cref="LiveSessionResolver.Resolve"/> takes the value rather than calling
/// <see cref="Environment.GetEnvironmentVariable(string)"/> itself, so these tests are
/// order-independent and do not mutate the process they run in — and so the precedence chain is
/// something a test can state rather than something it has to arrange.
/// </para>
/// </remarks>
public class LiveSessionResolverTests
{
    private const string Key = "32-character-with-lots-of-filler";
    private const string OtherKey = "another-32-character-api-key-abc";

    private static LiveSessionOptions Valid() => new()
    {
        ApiKey = Key,
        Dataset = "EQUS.MINI",
        Subscriptions =
        [
            new SubscriptionOptions { Schema = "mbp-1", StypeIn = "raw_symbol", Symbols = ["AAPL", "MSFT"] },
        ],
    };

    [Fact]
    public void Resolve_OverAValidSession_ProducesTheRealTypes()
    {
        var result = LiveSessionResolver.Resolve("equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);

        var session = result.Session;
        Assert.Equal("equities", session.Name);
        Assert.Equal(Key, session.ApiKey.Value);
        Assert.Equal("EQUS.MINI", session.Dataset);

        var subscription = Assert.Single(session.Subscriptions);
        Assert.Equal(Schema.Mbp1, subscription.Schema);
        Assert.Equal(SType.RawSymbol, subscription.StypeIn);
        Assert.Equal(["AAPL", "MSFT"], subscription.Symbols.ToArray());
        Assert.Null(subscription.Start);
    }

    [Fact]
    public void Resolve_WithNoStypeIn_DefaultsToRawSymbol()
    {
        // LiveClient's own default, restated here rather than left to chance: the wire default and
        // the configuration default must agree or a session behaves differently depending on
        // whether a key was written down.
        var options = Valid();
        options.Subscriptions[0].StypeIn = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SType.RawSymbol, Assert.Single(result.Session.Subscriptions).StypeIn);
    }

    [Fact]
    public void Resolve_WithTheAllSymbolsWireValue_ProducesSymbolsAll()
    {
        var options = Valid();
        options.Subscriptions[0].Symbols = [Symbols.AllWireValue];

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(SymbolsKind.All, Assert.Single(result.Session.Subscriptions).Symbols.Kind);
    }

    [Fact]
    public void Resolve_WithAReplayStart_ParsesItAsAnInstant()
    {
        var options = Valid();
        options.Subscriptions[0].Start = "2026-08-31T14:30:00Z";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(
            Instant.FromUtc(2026, 8, 31, 14, 30),
            Assert.Single(result.Session.Subscriptions).Start);
    }

    [Fact]
    public void Resolve_WithAnUnknownSchema_FailsAndNamesTheExactPath()
    {
        var options = Valid();
        options.Subscriptions[0].Schema = "mbp1";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Session);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Schema — ", failure);
        Assert.Contains("'mbp1'", failure);
    }

    [Fact]
    public void Resolve_ReportsEveryFailure_NotJustTheFirst()
    {
        // A configuration with four mistakes should take one edit-and-restart cycle, not four.
        var options = new LiveSessionOptions
        {
            Dataset = null,
            Subscriptions =
            [
                new SubscriptionOptions { Schema = "nope", StypeIn = "also-nope", Symbols = ["AAPL"] },
            ],
            Reconnect = new ReconnectOptions { InitialDelay = "one second" },
        };

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Equal(5, result.Failures.Length);
        Assert.All(result.Failures, f => Assert.StartsWith("Databento:", f));
        Assert.Contains(result.Failures, f => f.Contains(":ApiKey — "));
        Assert.Contains(result.Failures, f => f.Contains(":Dataset — "));
        Assert.Contains(result.Failures, f => f.Contains(":Subscriptions:0:Schema — "));
        Assert.Contains(result.Failures, f => f.Contains(":Subscriptions:0:StypeIn — "));
        Assert.Contains(result.Failures, f => f.Contains(":Reconnect:InitialDelay — "));
    }

    [Fact]
    public void Resolve_WithNoSubscriptions_Fails()
    {
        var options = Valid();
        options.Subscriptions.Clear();

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.StartsWith("Databento:Live:equities:Subscriptions — "));
    }

    [Fact]
    public void Resolve_WithNoSymbols_CarriesTheLibrarysOwnMessage()
    {
        // Symbols.From's message, with the path prefixed. Not a second copy of the rule: this
        // resolver never decides for itself what a valid symbol set is.
        var options = Valid();
        options.Subscriptions[0].Symbols = [];

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Symbols — ", failure);
        Assert.Contains("at least one symbol", failure);
    }

    [Theory]
    [InlineData("PT1S", 1)]
    [InlineData("PT30S", 30)]
    [InlineData("PT1M", 60)]
    [InlineData("PT1H30M", 5400)]
    public void Resolve_ParsesIso8601Durations(string text, int expectedSeconds)
    {
        var options = Valid();
        options.Reconnect.InitialDelay = text;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Duration.FromSeconds(expectedSeconds), result.Session.Reconnect.InitialDelay);
    }

    [Theory]
    // Parses as a Period and then cannot become a Duration: a month is not a fixed length.
    [InlineData("P1M")]
    [InlineData("P1Y")]
    // Parses to a negative duration. A backoff that runs backwards is not a preference.
    [InlineData("PT-5S")]
    // Not ISO-8601 at all. The third is NodaTime's own DurationPattern.Roundtrip form, which is a
    // plausible mistake precisely because it is what this repo uses everywhere else.
    [InlineData("30")]
    [InlineData("30s")]
    [InlineData("0:00:00:30")]
    public void Resolve_WithANonDuration_FailsAndNamesThePath(string text)
    {
        var options = Valid();
        options.Reconnect.MaxDelay = text;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:Reconnect:MaxDelay — ", failure);
    }

    [Fact]
    public void Resolve_TakesTheApiKeyFromTheSessionFirst()
    {
        var result = LiveSessionResolver.Resolve(
            "equities", Valid(), new DatabentoOptions { ApiKey = OtherKey }, "ignored-env-key-32-chars-long!!");

        Assert.True(result.Succeeded);
        Assert.Equal(Key, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheRootApiKey()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve(
            "equities", options, new DatabentoOptions { ApiKey = OtherKey }, null);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_FallsBackToTheEnvironmentVariableLast()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), OtherKey);

        Assert.True(result.Succeeded);
        Assert.Equal(OtherKey, result.Session.ApiKey.Value);
    }

    [Fact]
    public void Resolve_WithNoKeyAnywhere_NamesAllThreePlacesItLooked()
    {
        var options = Valid();
        options.ApiKey = null;

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("Databento:Live:equities:ApiKey", failure);
        Assert.Contains("Databento:ApiKey", failure);
        Assert.Contains(LiveSessionResolver.ApiKeyEnvironmentVariable, failure);
    }

    [Fact]
    public void Resolve_WithAMalformedKey_CarriesTheLibrarysMessageAndNotTheKey()
    {
        var options = Valid();
        options.ApiKey = "too-short";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("Databento:Live:equities:ApiKey — ", failure);
        Assert.Contains("exactly 32 characters", failure);
        // ApiKey's own constructor never puts the key in the message, and neither does this. A
        // validation failure is logged, and a logged credential is the failure this library's
        // redacted ToString exists to prevent.
        Assert.DoesNotContain("too-short", failure);
    }

    [Fact]
    public void Resolve_ParsesTheGatewayEndpoint()
    {
        var options = Valid();
        options.Gateway = "127.0.0.1:13000";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal("127.0.0.1:13000", result.Session.Gateway!.ToString());
    }

    [Fact]
    public void Resolve_ParsesAHostnameGatewayAsADnsEndPoint()
    {
        var options = Valid();
        options.Gateway = "lsg.databento.com:13000";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        var endpoint = Assert.IsType<System.Net.DnsEndPoint>(result.Session.Gateway);
        Assert.Equal("lsg.databento.com", endpoint.Host);
        Assert.Equal(13000, endpoint.Port);
    }

    [Fact]
    public void Resolve_WithNoGateway_LeavesItNullForLiveClientToDerive()
    {
        // LiveClient.Gateway null means "derive it from the dataset" via LiveGateway.For. The
        // resolver must not helpfully fill that in: deriving it twice is how the two would drift.
        var result = LiveSessionResolver.Resolve("equities", Valid(), new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Null(result.Session.Gateway);
    }

    [Fact]
    public void Resolve_ParsesCompressionAndSlowReaderBehaviourByTheirWireStrings()
    {
        var options = Valid();
        options.Compression = "zstd";
        options.SlowReaderBehavior = "skip";

        var result = LiveSessionResolver.Resolve("equities", options, new DatabentoOptions(), null);

        Assert.True(result.Succeeded);
        Assert.Equal(Compression.Zstd, result.Session.Compression);
        Assert.Equal(SlowReaderBehavior.Skip, result.Session.SlowReaderBehavior);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests`

Expected: FAIL — the option and resolver types do not exist.

- [ ] **Step 3: Write the options DTOs**

`Options/DatabentoOptions.cs`:

```csharp
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Configuration common to every Databento client in the container.</summary>
/// <remarks>
/// Bound from the section handed to <c>AddDatabento</c>, conventionally <c>Databento</c>.
/// </remarks>
public sealed class DatabentoOptions
{
    /// <summary>The conventional configuration section name: <c>Databento</c>.</summary>
    public const string DefaultSectionName = "Databento";

    /// <summary>
    /// The API key every client uses unless it names its own, or <see langword="null"/> to fall
    /// back to the <c>DATABENTO_API_KEY</c> environment variable.
    /// </summary>
    /// <remarks>
    /// A <see langword="string"/> here and an <see cref="DatabentoDotNet.ApiKey"/> everywhere
    /// else, and the asymmetry is the whole reason this type exists: <c>ApiKey</c> validates in
    /// its constructor and has no parameterless form, so a configuration binder cannot produce
    /// one. The crossing happens once, in <see cref="LiveSessionResolver"/>, where a bad key
    /// becomes a startup failure naming its configuration path rather than an exception from
    /// inside a binder.
    /// </remarks>
    public string? ApiKey { get; set; }
}
```

`Options/HistoricalOptions.cs`:

```csharp
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Configuration for the historical and reference clients, which share one transport.</summary>
public sealed class HistoricalOptions
{
    /// <summary>The API key, or <see langword="null"/> to use <see cref="DatabentoOptions.ApiKey"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// A base URL to send requests to instead of the gateway's, or <see langword="null"/> for the
    /// gateway. For a proxy or a test harness.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Text identifying the application, appended to this library's <c>User-Agent</c>.</summary>
    public string? UserAgentExtension { get; set; }

    /// <summary>
    /// How long a pooled connection may be reused before it is replaced, as an ISO-8601 duration.
    /// Defaults to <c>PT5M</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the reason the package registers an <c>IHttpClientFactory</c> handler at all.</b>
    /// <see cref="System.Net.Http.HttpClient"/>'s own handler leaves
    /// <c>PooledConnectionLifetime</c> infinite, so a singleton in a host that stays up for weeks
    /// keeps talking to whatever address <c>hist.databento.com</c> resolved to on its first
    /// request. Five minutes is what the .NET documentation recommends for a long-lived client.
    /// </para>
    /// <para>
    /// A <see langword="string"/> because <c>T:System.TimeSpan</c> is banned as a type repo-wide
    /// and NodaTime's <c>Duration</c> has nothing for a binder to fill. ISO-8601 is unambiguous
    /// across locales, which <c>InvariantGlobalization</c> makes a live concern.
    /// </para>
    /// </remarks>
    public string? PooledConnectionLifetime { get; set; }
}
```

There is deliberately **no `Gateway` property**: `HistoricalGateway` has exactly one member,
`Bo1` (`HistoricalGateway.cs:26`), so a knob with one setting would be a knob that does nothing.
`BaseUrl` covers the override case, which is what upstream's `with_url` is for.

`Options/LiveSessionOptions.cs`:

```csharp
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>One named live session: what to stream, from where, and how to recover.</summary>
/// <remarks>
/// <para>
/// <b>Every property is a <see langword="string"/>, an <see langword="int"/>, a
/// <see langword="bool"/>, or a list of those</b>, and that is forced rather than chosen.
/// <c>T:System.TimeSpan</c> is banned as a type, so RS0030 fires on the property declaration and
/// not merely on <c>TimeSpan.FromSeconds</c>; NodaTime's <c>Duration</c> has no
/// <c>TypeConverter</c> and no settable properties, so a binder fills it with nothing;
/// <c>ApiKey</c> validates in its constructor; <c>Symbols</c> has no binder-shaped form at all;
/// and <c>Schema</c> and <c>SType</c> would bind by their C# names — <c>Mbp1</c> rather than
/// <c>mbp-1</c> — making the configuration file the only place in the Databento ecosystem where
/// the name is spelled differently.
/// </para>
/// <para>
/// All of them are therefore <em>resolved</em> rather than bound, by
/// <see cref="LiveSessionResolver"/>, which is also what
/// <see cref="LiveSessionValidator"/> calls. One crossing, two callers.
/// </para>
/// </remarks>
public sealed class LiveSessionOptions
{
    /// <summary>The session's API key, or <see langword="null"/> to use the root's.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The dataset, as its wire name — for example <c>EQUS.MINI</c>.</summary>
    public string? Dataset { get; set; }

    /// <summary>What to subscribe to. At least one.</summary>
    public IList<SubscriptionOptions> Subscriptions { get; set; } = [];

    /// <summary>How to recover from a dropped connection.</summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>Whether to ask the gateway to stamp each record with its send time.</summary>
    public bool SendTsOut { get; set; }

    /// <summary>Session compression, as a wire string: <c>none</c> or <c>zstd</c>. Defaults to <c>none</c>.</summary>
    public string? Compression { get; set; }

    /// <summary>What the gateway does when this client reads too slowly: <c>warn</c> or <c>skip</c>.</summary>
    public string? SlowReaderBehavior { get; set; }

    /// <summary>The heartbeat interval as an ISO-8601 duration, or <see langword="null"/> for the gateway's default.</summary>
    public string? HeartbeatInterval { get; set; }

    /// <summary>
    /// How long a read may find nothing before the connection is treated as dead, as an ISO-8601
    /// duration, or <see langword="null"/> for <c>LiveClient</c>'s own derivation from the
    /// heartbeat interval.
    /// </summary>
    /// <remarks>
    /// This is what turns a silent gateway into a <c>HeartbeatTimeoutException</c>, which is the
    /// transient failure the reconnect policy exists for. Lowering it in a test is also how
    /// <c>LiveSessionReconnectTests</c> provokes one without waiting thirty-five seconds.
    /// </remarks>
    public string? ReadTimeout { get; set; }

    /// <summary>
    /// The gateway to connect to as <c>host:port</c>, or <see langword="null"/> to derive it from
    /// <see cref="Dataset"/>.
    /// </summary>
    /// <remarks>
    /// Left <see langword="null"/> this stays null on the resolved session, so
    /// <c>LiveClient</c> derives it through <c>LiveGateway.For</c>. The resolver deliberately does
    /// not derive it too: two derivations of one value are two things that can drift.
    /// </remarks>
    public string? Gateway { get; set; }
}

/// <summary>One subscription within a session.</summary>
public sealed class SubscriptionOptions
{
    /// <summary>The schema, as its wire string — <c>mbp-1</c>, <c>trades</c>, <c>ohlcv-1s</c>.</summary>
    public string? Schema { get; set; }

    /// <summary>The input symbology, as its wire string. Defaults to <c>raw_symbol</c>.</summary>
    public string? StypeIn { get; set; }

    /// <summary>
    /// The symbols. A single entry of <c>ALL_SYMBOLS</c> means the whole dataset.
    /// </summary>
    public IList<string> Symbols { get; set; } = [];

    /// <summary>
    /// An ISO-8601 instant to replay from before going live, or <see langword="null"/> for
    /// real-time only.
    /// </summary>
    public string? Start { get; set; }

    /// <summary>Whether to ask for a book snapshot first. Only the <c>mbo</c> schema supports it.</summary>
    public bool UseSnapshot { get; set; }
}

/// <summary>How a session recovers from a dropped connection.</summary>
public sealed class ReconnectOptions
{
    /// <summary>Whether to reconnect at all. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The first backoff delay, as an ISO-8601 duration. Defaults to <c>PT1S</c>.</summary>
    public string InitialDelay { get; set; } = "PT1S";

    /// <summary>The backoff ceiling, as an ISO-8601 duration. Defaults to <c>PT30S</c>.</summary>
    public string MaxDelay { get; set; } = "PT30S";

    /// <summary>
    /// How many <em>consecutive</em> failures to tolerate before giving up. Defaults to 10.
    /// </summary>
    /// <remarks>
    /// Consecutive, and the counter resets on a successful start — so a gateway that flaps every
    /// ten minutes reconnects indefinitely. That is deliberate: the alternative silently stops a
    /// worker overnight. <b>Every reconnect starts a newly billed session</b>, which is what this
    /// bound is really bounding.
    /// </remarks>
    public int MaxAttempts { get; set; } = 10;
}
```

- [ ] **Step 4: Write the resolved types**

`Options/ResolvedLiveSession.cs`:

```csharp
using System.Collections.Immutable;
using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// A live session's configuration with every value converted to the type the library actually
/// takes. Produced only by <see cref="LiveSessionResolver"/>.
/// </summary>
/// <remarks>
/// Public, and constructible directly, which is what lets <c>LiveSessionRunner</c> be driven by a
/// test with no host, no container and no configuration provider — the property the whole testing
/// strategy rests on.
/// </remarks>
public sealed record ResolvedLiveSession
{
    /// <summary>The session's registration name, which is also its configuration key.</summary>
    public required string Name { get; init; }

    /// <summary>The validated API key.</summary>
    public required ApiKey ApiKey { get; init; }

    /// <summary>The dataset.</summary>
    public required string Dataset { get; init; }

    /// <summary>The subscriptions to send, in order, after authenticating.</summary>
    public required ImmutableArray<Subscription> Subscriptions { get; init; }

    /// <summary>The reconnection policy.</summary>
    public required ResolvedReconnect Reconnect { get; init; }

    /// <summary>Whether the gateway stamps each record with its send time.</summary>
    public bool SendTsOut { get; init; }

    /// <summary>Session compression.</summary>
    public Compression Compression { get; init; } = Compression.None;

    /// <summary>What the gateway does when this client reads too slowly, or <see langword="null"/> for its default.</summary>
    public SlowReaderBehavior? SlowReaderBehavior { get; init; }

    /// <summary>The heartbeat interval, or <see langword="null"/> for the gateway's default.</summary>
    public Duration? HeartbeatInterval { get; init; }

    /// <summary>The read timeout, or <see langword="null"/> for <c>LiveClient</c>'s own derivation.</summary>
    public Duration? ReadTimeout { get; init; }

    /// <summary>The gateway, or <see langword="null"/> to let <c>LiveClient</c> derive it from <see cref="Dataset"/>.</summary>
    public EndPoint? Gateway { get; init; }
}

/// <summary>A reconnection policy with its durations parsed.</summary>
public sealed record ResolvedReconnect
{
    /// <summary>The default policy: enabled, one second to thirty, ten consecutive attempts.</summary>
    public static ResolvedReconnect Default { get; } = new()
    {
        Enabled = true,
        InitialDelay = Duration.FromSeconds(1),
        MaxDelay = Duration.FromSeconds(30),
        MaxAttempts = 10,
    };

    /// <summary>Whether to reconnect at all.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The first backoff delay.</summary>
    public required Duration InitialDelay { get; init; }

    /// <summary>The backoff ceiling.</summary>
    public required Duration MaxDelay { get; init; }

    /// <summary>How many consecutive failures to tolerate. The counter resets on a successful start.</summary>
    public required int MaxAttempts { get; init; }
}
```

- [ ] **Step 5: Write the resolver**

`Options/LiveSessionResolver.cs`. The shape, with every failure path present — write it in full,
not as a sketch:

```csharp
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;
using NodaTime.Text;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Turns a bound <see cref="LiveSessionOptions"/> into a <see cref="ResolvedLiveSession"/>,
/// collecting every failure rather than stopping at the first.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only crossing, and both callers use it.</b>
/// <see cref="LiveSessionValidator"/> calls it at startup and the registration calls it when it
/// builds a runner, so a configuration that validates is a configuration that resolves — because
/// no second path exists to disagree. That is the rule <c>DbnTime</c> already enforces for the
/// <c>UndefTimestamp</c> sentinel, applied to a different boundary for the same reason.
/// </para>
/// <para>
/// <b>It never re-implements a check the library already makes.</b> A key goes through
/// <c>new ApiKey(text)</c> and a symbol list through <c>Symbols.From</c>; when either throws, the
/// message is kept and the configuration path is prefixed to it. A resolver that decided for
/// itself what a valid key looks like would be a second copy of that rule, and the copy that
/// silently disagrees is the one nobody is looking at.
/// </para>
/// <para>
/// <b>Every failure names its configuration path</b>, because the person reading the message is
/// looking at an <c>appsettings.json</c>, not at this assembly:
/// <c>Databento:Live:equities:Subscriptions:0:Schema — 'mbp1' is not a Databento schema.</c>
/// </para>
/// </remarks>
public static class LiveSessionResolver
{
    /// <summary>The environment variable consulted when no configuration supplies a key.</summary>
    public const string ApiKeyEnvironmentVariable = "DATABENTO_API_KEY";

    /// <summary>The configuration path a named session binds from: <c>Databento:Live:{name}</c>.</summary>
    public static string PathFor(string name) =>
        $"{DatabentoOptions.DefaultSectionName}:Live:{name}";

    /// <summary>Resolves one session, or reports why it cannot be resolved.</summary>
    /// <param name="name">The session's registration name.</param>
    /// <param name="options">The bound options.</param>
    /// <param name="root">The root options, consulted for a key the session does not carry.</param>
    /// <param name="environmentApiKey">
    /// The value of <see cref="ApiKeyEnvironmentVariable"/>, or <see langword="null"/>. A
    /// parameter rather than an ambient read, so that the precedence chain is something a test
    /// can state and this method mutates nothing.
    /// </param>
    public static LiveSessionResolutionResult Resolve(
        string name,
        LiveSessionOptions options,
        DatabentoOptions root,
        string? environmentApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var path = PathFor(name);
        var failures = ImmutableArray.CreateBuilder<string>();

        var apiKey = ResolveApiKey(path, options, root, environmentApiKey, failures);
        var dataset = Required(options.Dataset, $"{path}:Dataset", "the dataset to stream, for example 'EQUS.MINI'", failures);
        var subscriptions = ResolveSubscriptions(path, options, failures);
        var reconnect = ResolveReconnect(path, options.Reconnect, failures);
        var compression = ResolveCompression(path, options.Compression, failures);
        var slowReader = ResolveSlowReader(path, options.SlowReaderBehavior, failures);
        var heartbeat = ResolveOptionalDuration($"{path}:HeartbeatInterval", options.HeartbeatInterval, failures);
        var readTimeout = ResolveOptionalDuration($"{path}:ReadTimeout", options.ReadTimeout, failures);
        var gateway = ResolveGateway(path, options.Gateway, failures);

        if (failures.Count > 0)
        {
            return LiveSessionResolutionResult.Failed(failures.ToImmutable());
        }

        return LiveSessionResolutionResult.Succeeded(new ResolvedLiveSession
        {
            Name = name,
            ApiKey = apiKey!,
            Dataset = dataset!,
            Subscriptions = subscriptions,
            Reconnect = reconnect,
            SendTsOut = options.SendTsOut,
            Compression = compression,
            SlowReaderBehavior = slowReader,
            HeartbeatInterval = heartbeat,
            ReadTimeout = readTimeout,
            Gateway = gateway,
        });
    }
}
```

The private helpers, each of which is one rule:

| Helper | Rule |
|---|---|
| `ResolveApiKey` | session → root → environment; then `new ApiKey(text)` inside a `try`, catching `ArgumentException` and prefixing `$"{path}:ApiKey — "`. The "no key anywhere" message names all three places it looked. |
| `Required` | `null`/whitespace → `$"{path} — missing. Set it to {what}."` |
| `ResolveSubscriptions` | empty list → one failure at `$"{path}:Subscriptions"`. Otherwise index each: `$"{path}:Subscriptions:{i}:Schema"` etc. `WireStrings.TryParseSchema` / `TryParseSType`; `StypeIn` null → `SType.RawSymbol`. `Symbols`: a single entry equal to `Symbols.AllWireValue` → `Symbols.All`, otherwise `Symbols.From(list)` inside a `try`. `Start` through `ResolveOptionalInstant`. Builds a `Subscription` with `Id = null` so `SubscribeAsync` assigns it. |
| `ResolveReconnect` | three `ResolveDuration` calls plus `MaxAttempts < 1` → a failure. `InitialDelay > MaxDelay` → a failure at `$"{path}:Reconnect:InitialDelay"` saying so. |
| `ResolveDuration` | `PeriodPattern.NormalizingIso.Parse(text)`; `!Success` → failure; `Period.Months != 0 \|\| Period.Years != 0` → failure saying a month is not a fixed length; `ToDuration() < Duration.Zero` → failure. |
| `ResolveOptionalInstant` | `InstantPattern.ExtendedIso.Parse(text)`; `!Success` → failure. |
| `ResolveCompression` / `ResolveSlowReader` | `WireStrings.TryParseCompression` / `SlowReaderBehaviorWireStrings.TryParse`; null → the default. |
| `ResolveGateway` | null → `null`. `IPEndPoint.TryParse(text, out var ip)` → `ip`. Otherwise split on the **last** `:`, `int.TryParse` the port with `CultureInfo.InvariantCulture`, → `new DnsEndPoint(host, port)`. Anything else → a failure naming the expected `host:port` form. |

A failure message never contains the value it rejected when that value is a credential. Every other
message quotes it, because `'mbp1' is not a Databento schema` is what makes the message actionable.

And the result type, in the same file:

```csharp
/// <summary>The outcome of resolving one session: the session, or every reason it could not be.</summary>
public sealed class LiveSessionResolutionResult
{
    private LiveSessionResolutionResult(ResolvedLiveSession? session, ImmutableArray<string> failures)
    {
        Session = session;
        Failures = failures;
    }

    /// <summary>The resolved session, or <see langword="null"/> when resolution failed.</summary>
    public ResolvedLiveSession? Session { get; }

    /// <summary>Every failure, each naming its configuration path. Empty on success.</summary>
    public ImmutableArray<string> Failures { get; }

    /// <summary>Whether the session resolved.</summary>
    [MemberNotNullWhen(true, nameof(Session))]
    public bool Succeeded => Session is not null;

    internal static LiveSessionResolutionResult Succeeded(ResolvedLiveSession session) => new(session, []);

    internal static LiveSessionResolutionResult Failed(ImmutableArray<string> failures) => new(null, failures);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests`

Expected: PASS, including the two `ConfigurationBindingTests` from Task 3, which compile now that
`LiveSessionOptions` exists.

- [ ] **Step 7: Update the public API baseline**

The build fails with RS0016 for every new public member until the baseline lists them. Generate the
entries rather than typing them: build, then apply the analyzer's own code fix, or copy the symbol
names out of the RS0016 messages into
`src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`. Sort them the way the other four
baseline files are sorted — types first, then members, alphabetically.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS, 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "feat(extensions): the options model and its one conversion path

Every option is a string, int, bool or a list of those, and that is forced:
T:System.TimeSpan is banned as a type so an options DTO cannot declare one,
Duration has nothing for a binder to fill, ApiKey validates in its constructor,
Symbols has no binder-shaped form, and Schema and SType would bind by their C#
names — 'Mbp1' rather than 'mbp-1', making the config file the only place in
the ecosystem where the name is spelled differently.

LiveSessionResolver is the one crossing. It never re-implements a check the
library already makes: a key goes through new ApiKey(text) and a symbol list
through Symbols.From, and when either throws the message is kept and the
configuration path is prefixed to it.

ISO-8601 durations are parsed by PeriodPattern.NormalizingIso, not
DurationPattern.Roundtrip — the latter does not parse \"PT30S\" at all. Two
guards beyond parse success: non-zero months or years cannot become a Duration,
and a negative backoff is rejected.

Refs #<registration>"
```

---

## Task 5: Validation, and the registration surface

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/HistoricalResolver.cs`
  (+ `ResolvedHistorical`, `HistoricalResolutionResult`)
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/LiveSessionValidator.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Options/HistoricalValidator.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/ServiceCollectionExtensions.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/DatabentoLiveBuilder.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/OptionsValidationTests.cs`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/RegistrationTests.cs`

**Interfaces:**
- Consumes: everything Task 4 produced; `HistoricalClient.Handler` / `DisposesHandler` from Task 2.
- Produces:
  ```csharp
  namespace Microsoft.Extensions.DependencyInjection;
  public static class DatabentoServiceCollectionExtensions
  {
      public static IServiceCollection AddDatabento(this IServiceCollection services);
      public static IServiceCollection AddDatabento(this IServiceCollection services, string sectionPath);
      public static IServiceCollection AddDatabento(this IServiceCollection services, IConfigurationSection section);

      public static IServiceCollection AddDatabentoHistorical(this IServiceCollection services);
      public static IServiceCollection AddDatabentoHistorical(this IServiceCollection services, Action<HistoricalOptions> configure);
      public static IServiceCollection AddDatabentoReference(this IServiceCollection services);

      public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services);
      public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name);
      public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name, Action<LiveSessionOptions> configure);
  }

  namespace DatabentoDotNet.Extensions.Hosting;
  public sealed class DatabentoLiveBuilder
  {
      public const string DefaultSessionName = "Default";
      public string Name { get; }
      public IServiceCollection Services { get; }
      public DatabentoLiveBuilder AddRecordHandler<THandler>() where THandler : class, ILiveRecordHandler;
      public DatabentoLiveBuilder AddRecordHandler(Func<IServiceProvider, ILiveRecordHandler> implementationFactory);
      // AddHealthCheck arrives in Task 10.
  }
  ```

  Task 7 supplies `ILiveRecordHandler`; **write a one-line placeholder for it in this task** so the
  builder compiles, and let Task 7 fill in its documentation.

  The keyed registrations Tasks 7–10 read:
  ```
  keyed singleton  ILiveRecordHandler   key = session name
  keyed singleton  LiveSessionRunner    key = session name        (Task 9 adds the factory)
  singleton        IHostedService       one per session           (Task 9 adds it)
  ```

**Four decisions, each of which someone will otherwise reverse.**

1. **Binding is by *path*, never by a carried `IConfiguration`.**
   `AddDatabento(IConfigurationSection)` reads `section.Path` and forwards to the string overload;
   the path is stashed in the `IServiceCollection` as an internal marker singleton that later
   `Add*` calls read at *registration* time. `BindConfiguration(path)` then resolves `IConfiguration`
   from the container when options are built. This is what makes `AddDatabentoHistorical()` — with
   no arguments and no access to a service provider — able to bind `Databento:Historical`.
2. **`TryAddSingleton` throughout, so `AddDatabentoHistorical()` and `AddDatabentoReference()`
   compose in either order.** `AddDatabentoReference()` calls `AddDatabentoHistorical()` first;
   if the consumer already called it, the `TryAdd` is a no-op and both clients end up on one
   `HistoricalClient`, one `HttpClient`, and one connection pool. That is the spec's §1 promise,
   implemented by the one method that makes it order-independent.
3. **`DisposesHandler = false` on the registered `HistoricalClient`.** The handler comes from
   `IHttpMessageHandlerFactory`, which pools handlers across clients and rotates them on its own
   schedule. Disposing one out from under it would break every other client sharing it. This is
   the property Task 2 exists to provide, used the one way it is meant to be used.
4. **`ValidateOnStart()`, not lazy validation.** A wrong key should fail
   `host.StartAsync()` with a message, not fail the first record read at 09:30. Verified:
   `ValidateOnStart` raises `OptionsValidationException` from `Host.StartAsync` with every failure
   in `Failures`.

- [ ] **Step 1: Write the failing validation tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/OptionsValidationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Startup validation: a configuration that is wrong stops the host, and the message says where
/// in the configuration file to look.
/// </summary>
/// <remarks>
/// <b>The validator and the runner share one conversion path</b>, so these tests are also what
/// establishes that a session which validates is a session which resolves.
/// <see cref="LiveSessionValidator"/> holds no rules of its own — it calls
/// <see cref="LiveSessionResolver.Resolve"/> and turns the failure list into a
/// <see cref="ValidateOptionsResult"/>.
/// </remarks>
public class OptionsValidationTests
{
    private const string Key = "32-character-with-lots-of-filler";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static IHost Host(string json)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Flatten(json));
        builder.Services.AddDatabento();
        builder.Services.AddDatabentoLive("equities").AddRecordHandler<NullHandler>();
        return builder.Build();
    }

    // A helper that turns the JSON in each test into the flat key/value pairs an in-memory
    // provider takes. Written out in the test project rather than reaching for a JSON file, so a
    // test's configuration is visible in the test.
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(string json) => /* … */;

    [Fact]
    public async Task StartAsync_WithAValidSession_Boots()
    {
        using var host = Host($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Gateway": "127.0.0.1:1",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ],
                "Reconnect": { "Enabled": false } } } } }
            """);

        // Options validation runs before any hosted service starts, so this reaches the point of
        // trying to connect. The session itself is Task 7's subject; what is asserted here is that
        // validation did not stop it.
        var options = host.Services.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>();
        Assert.Equal("EQUS.MINI", options.Get("equities").Dataset);
    }

    [Fact]
    public async Task StartAsync_WithAnUnknownSchema_FailsTheBootAndNamesThePath()
    {
        using var host = Host($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "mbp1", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("Databento:Live:equities:Subscriptions:0:Schema — ", failure);
    }

    [Fact]
    public async Task StartAsync_WithNoApiKeyAnywhere_FailsTheBoot()
    {
        using var host = Host("""
            { "Databento": { "Live": { "equities": {
                "Dataset": "EQUS.MINI",
                "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        Assert.Contains(exception.Failures, f => f.Contains("ApiKey"));
    }

    [Fact]
    public async Task StartAsync_ReportsEveryFailureAtOnce()
    {
        // One restart to see four mistakes, not four restarts. The reason the resolver collects
        // rather than throwing on the first.
        using var host = Host("""
            { "Databento": { "Live": { "equities": {
                "Subscriptions": [ { "Schema": "nope", "StypeIn": "also-nope", "Symbols": ["AAPL"] } ] } } } }
            """);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        Assert.Equal(4, exception.Failures.Count());
    }

    [Fact]
    public async Task StartAsync_WithTwoSessions_ValidatesEachAgainstItsOwnPath()
    {
        // Each session registers its own IValidateOptions<LiveSessionOptions>, and each skips a
        // name that is not its own. Getting that wrong makes one session's mistake stop the other.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(Flatten($$"""
            { "Databento": { "ApiKey": "{{Key}}", "Live": {
                "equities": { "Dataset": "EQUS.MINI",  "Subscriptions": [ { "Schema": "trades", "Symbols": ["AAPL"] } ] },
                "futures":  { "Dataset": "GLBX.MDP3", "Subscriptions": [ { "Schema": "nope",   "Symbols": ["ESH6"] } ] } } } }
            """));
        builder.Services.AddDatabento();
        builder.Services.AddDatabentoLive("equities").AddRecordHandler<NullHandler>();
        builder.Services.AddDatabentoLive("futures").AddRecordHandler<NullHandler>();

        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(Cancel));

        var failure = Assert.Single(exception.Failures);
        Assert.StartsWith("Databento:Live:futures:", failure);
    }
}
```

`NullHandler` is a two-line `ILiveRecordHandler` in the test project; Task 7 replaces it with
`RecordingHandler` where a test needs to see the records.

- [ ] **Step 2: Write the failing registration tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/RegistrationTests.cs`:

```csharp
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// A real <see cref="ServiceProvider"/> resolves what was registered, and two named sessions stay
/// independent.
/// </summary>
/// <remarks>
/// A real container rather than assertions about <see cref="ServiceDescriptor"/>s: what a consumer
/// experiences is <c>GetRequiredService</c> returning something, and a descriptor list can be
/// right while the graph it describes fails to build.
/// </remarks>
public class RegistrationTests
{
    private const string Key = "32-character-with-lots-of-filler";

    private static ServiceProvider Provider(Action<IServiceCollection> register)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Databento:ApiKey"] = Key,
                ["Databento:Live:equities:Dataset"] = "EQUS.MINI",
                ["Databento:Live:equities:Subscriptions:0:Schema"] = "trades",
                ["Databento:Live:equities:Subscriptions:0:Symbols:0"] = "AAPL",
                ["Databento:Live:futures:Dataset"] = "GLBX.MDP3",
                ["Databento:Live:futures:Subscriptions:0:Schema"] = "mbp-1",
                ["Databento:Live:futures:Subscriptions:0:Symbols:0"] = "ESH6",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDatabento();
        register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddDatabentoHistorical_RegistersASingletonCarryingTheKey()
    {
        using var provider = Provider(services => services.AddDatabentoHistorical());

        var client = provider.GetRequiredService<HistoricalClient>();

        Assert.Equal(Key, client.ApiKey.Value);
        Assert.NotNull(client.Handler);
        // The factory owns the handler's lifetime and rotates it on its own schedule.
        Assert.False(client.DisposesHandler);
        Assert.Same(client, provider.GetRequiredService<HistoricalClient>());
    }

    [Fact]
    public void AddDatabentoReference_ReusesTheHistoricalTransport()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoHistorical();
            services.AddDatabentoReference();
        });

        Assert.Same(
            provider.GetRequiredService<HistoricalClient>(),
            provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public void AddDatabentoReference_Alone_RegistersTheTransportItself()
    {
        // Neither call is a prerequisite of the other. This is the half that would break if
        // AddDatabentoReference assumed AddDatabentoHistorical had already run.
        using var provider = Provider(services => services.AddDatabentoReference());

        Assert.NotNull(provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public void AddDatabentoReferenceThenHistorical_StillYieldsOneTransport()
    {
        // The other order, because TryAddSingleton is what makes both orders equivalent and
        // nothing else in the registration does.
        using var provider = Provider(services =>
        {
            services.AddDatabentoReference();
            services.AddDatabentoHistorical();
        });

        Assert.Same(
            provider.GetRequiredService<HistoricalClient>(),
            provider.GetRequiredService<ReferenceClient>().Transport);
    }

    [Fact]
    public void AddDatabentoLive_RegistersTheHandlerUnderTheSessionName()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>();
            services.AddDatabentoLive("futures").AddRecordHandler<FuturesHandler>();
        });

        Assert.IsType<EquitiesHandler>(provider.GetRequiredKeyedService<ILiveRecordHandler>("equities"));
        Assert.IsType<FuturesHandler>(provider.GetRequiredKeyedService<ILiveRecordHandler>("futures"));
    }

    [Fact]
    public void AddDatabentoLive_BindsEachSessionFromItsOwnConfigurationKey()
    {
        using var provider = Provider(services =>
        {
            services.AddDatabentoLive("equities").AddRecordHandler<EquitiesHandler>();
            services.AddDatabentoLive("futures").AddRecordHandler<FuturesHandler>();
        });

        var monitor = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>();
        Assert.Equal("EQUS.MINI", monitor.Get("equities").Dataset);
        Assert.Equal("GLBX.MDP3", monitor.Get("futures").Dataset);
    }

    [Fact]
    public void AddDatabentoLive_WithNoName_UsesTheLiteralDefaultName()
    {
        // Databento:Live:{name} in every case, so Databento:Live:Dataset and
        // Databento:Live:equities are never siblings of different kinds.
        var services = new ServiceCollection();
        var builder = services.AddDatabentoLive();

        Assert.Equal(DatabentoLiveBuilder.DefaultSessionName, builder.Name);
        Assert.Equal("Databento:Live:Default", LiveSessionResolver.PathFor(builder.Name));
    }

    [Fact]
    public void AddDatabentoLive_WithALambda_OverridesTheBoundValue()
    {
        using var provider = Provider(services =>
            services.AddDatabentoLive("equities", options => options.Dataset = "XNAS.ITCH")
                    .AddRecordHandler<EquitiesHandler>());

        Assert.Equal(
            "XNAS.ITCH",
            provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities").Dataset);
    }

    private sealed class EquitiesHandler : ILiveRecordHandler { /* … */ }

    private sealed class FuturesHandler : ILiveRecordHandler { /* … */ }
}
```

- [ ] **Step 3: Run both test files to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests`

Expected: FAIL — `AddDatabento`, `DatabentoLiveBuilder` and `ILiveRecordHandler` do not exist.

- [ ] **Step 4: Write `HistoricalResolver` and the two validators**

`HistoricalResolver` mirrors `LiveSessionResolver` exactly — same result shape, same
path-prefixing, same rule about never re-implementing a library check. It resolves four things:

| From | To | Failure path |
|---|---|---|
| `ApiKey` ?? root ?? environment | `ApiKey` | `Databento:Historical:ApiKey` |
| `BaseUrl` | `Uri?`, absolute | `Databento:Historical:BaseUrl` |
| `UserAgentExtension` | passthrough | — |
| `PooledConnectionLifetime` ?? `"PT5M"` | `Duration`, positive | `Databento:Historical:PooledConnectionLifetime` |

The two validators are each about ten lines and hold no rules of their own:

```csharp
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Validates one named <see cref="LiveSessionOptions"/> at startup by resolving it.
/// </summary>
/// <remarks>
/// <b>This type holds no rules.</b> It calls <see cref="LiveSessionResolver.Resolve"/> and turns
/// its failure list into a <see cref="ValidateOptionsResult"/>, which is what makes "a
/// configuration that validates is a configuration that resolves" true by construction rather
/// than by two lists being kept in step.
/// </remarks>
public sealed class LiveSessionValidator : IValidateOptions<LiveSessionOptions>
{
    private readonly string _name;
    private readonly IOptions<DatabentoOptions> _root;

    /// <summary>Creates a validator for one session.</summary>
    public LiveSessionValidator(string name, IOptions<DatabentoOptions> root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(root);

        _name = name;
        _root = root;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LiveSessionOptions options)
    {
        // Skip, not Success. Every session registers one of these, so each is asked about every
        // other session's options; answering Success for a name this validator knows nothing
        // about would report a bad configuration as a good one.
        if (!string.Equals(name, _name, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        var result = LiveSessionResolver.Resolve(
            _name,
            options,
            _root.Value,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        return result.Succeeded
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(result.Failures);
    }
}
```

- [ ] **Step 5: Write `DatabentoLiveBuilder`**

```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Configures one named live session. Returned by <c>AddDatabentoLive</c>.
/// </summary>
public sealed class DatabentoLiveBuilder
{
    /// <summary>
    /// The name the no-argument <c>AddDatabentoLive</c> overload uses: <c>Default</c>.
    /// </summary>
    /// <remarks>
    /// A literal name rather than an empty one, so the configuration path is
    /// <c>Databento:Live:{name}</c> in every case. The alternative — the single session's keys
    /// directly under <c>Databento:Live</c> and named ones beneath it — makes
    /// <c>Databento:Live:Dataset</c> and <c>Databento:Live:equities</c> siblings of different
    /// kinds, which is ambiguous to read and worse to report an error against.
    /// </remarks>
    public const string DefaultSessionName = "Default";

    internal DatabentoLiveBuilder(IServiceCollection services, string name)
    {
        Services = services;
        Name = name;
    }

    /// <summary>The session's name, which is also its configuration key and its service key.</summary>
    public string Name { get; }

    /// <summary>The collection this session was registered into.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Registers the handler this session's records are dispatched to.</summary>
    /// <typeparam name="THandler">The handler type. Constructed once, as a singleton.</typeparam>
    /// <remarks>
    /// <b>A singleton, and that is the dispatch contract rather than a default.</b> A scope per
    /// record would allocate, in the one package whose reason to exist is that it does not. A
    /// handler needing scoped services opens a scope inside
    /// <see cref="ILiveRecordHandler.OnFlushAsync"/>, which is where I/O belongs anyway.
    /// </remarks>
    public DatabentoLiveBuilder AddRecordHandler<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, ILiveRecordHandler
    {
        Services.AddKeyedSingleton<ILiveRecordHandler, THandler>(Name);
        return this;
    }

    /// <summary>Registers the handler this session's records are dispatched to, built by a factory.</summary>
    public DatabentoLiveBuilder AddRecordHandler(Func<IServiceProvider, ILiveRecordHandler> implementationFactory)
    {
        ArgumentNullException.ThrowIfNull(implementationFactory);
        Services.AddKeyedSingleton<ILiveRecordHandler>(Name, (provider, _) => implementationFactory(provider));
        return this;
    }
}
```

The `[DynamicallyAccessedMembers]` attribute is not optional decoration: without it
`AddKeyedSingleton<TService, TImplementation>` reports IL2091 under `EnableTrimAnalyzer`, which
`TreatWarningsAsErrors` makes a build failure. Add it when the build says so, not before, so the
requirement is recorded by the compiler rather than by belief.

- [ ] **Step 6: Write a placeholder `ILiveRecordHandler`**

Task 6 documents it. For now, the exact signature, because everything above compiles against it:

```csharp
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Receives the records a live session decodes.</summary>
public interface ILiveRecordHandler
{
    /// <summary>Called once per record, inside the drain.</summary>
    void OnRecord(scoped RecordRef record);

    /// <summary>Called once per socket fill, after the drain.</summary>
    ValueTask OnFlushAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 7: Write `ServiceCollectionExtensions`**

Namespace `Microsoft.Extensions.DependencyInjection`, per the near-universal convention, so `Add*`
appears on `IServiceCollection` with no extra `using`. The class is
`DatabentoServiceCollectionExtensions`.

The section-path marker, and the helper that reads it:

```csharp
    /// <summary>
    /// Carries the configuration section path from <c>AddDatabento</c> to the <c>Add*</c> calls
    /// that follow it.
    /// </summary>
    /// <remarks>
    /// A marker in the service collection rather than a captured <c>IConfiguration</c>, because
    /// <c>AddDatabentoHistorical()</c> takes no arguments and has no service provider to resolve
    /// one from — it runs at registration time. A path is all that has to travel:
    /// <c>BindConfiguration</c> resolves the <c>IConfiguration</c> itself, from the container,
    /// when the options are actually built.
    /// </remarks>
    private sealed class DatabentoSectionPath(string value)
    {
        public string Value { get; } = value;
    }

    private static string SectionPathFor(IServiceCollection services) =>
        services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(DatabentoSectionPath))
                ?.ImplementationInstance is DatabentoSectionPath marker
            ? marker.Value
            : DatabentoOptions.DefaultSectionName;
```

`AddDatabento`:

```csharp
    public static IServiceCollection AddDatabento(this IServiceCollection services) =>
        AddDatabento(services, DatabentoOptions.DefaultSectionName);

    public static IServiceCollection AddDatabento(this IServiceCollection services, IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        // The path, not the section. See DatabentoSectionPath.
        return AddDatabento(services, section.Path);
    }

    public static IServiceCollection AddDatabento(this IServiceCollection services, string sectionPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        services.AddSingleton(new DatabentoSectionPath(sectionPath));
        services.AddOptions<DatabentoOptions>().BindConfiguration(sectionPath);
        return services;
    }
```

`AddDatabentoHistorical` — the named handler, the pooled lifetime, and the client:

```csharp
    private const string HttpClientName = "DatabentoDotNet.Historical";

    public static IServiceCollection AddDatabentoHistorical(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var path = SectionPathFor(services);
        services.AddOptions<HistoricalOptions>().BindConfiguration($"{path}:Historical").ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<HistoricalOptions>, HistoricalValidator>());

        // The whole reason this package touches HTTP at all. HttpClient's own SocketsHttpHandler
        // leaves PooledConnectionLifetime infinite, so a singleton in a host that stays up for
        // weeks keeps talking to whatever address hist.databento.com resolved to on its first
        // request. The factory rotates the handler on the schedule set here.
        services.AddHttpClient(HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
                {
                    // Duration.ToTimeSpan(), never the banned type by name. HistoricalClient.cs
                    // already relies on the same rule for Timeout.InfiniteTimeSpan.
                    PooledConnectionLifetime = Resolve(provider).PooledConnectionLifetime.ToTimeSpan(),
                });

        services.TryAddSingleton(provider =>
        {
            var resolved = Resolve(provider);
            return new HistoricalClient
            {
                ApiKey = resolved.ApiKey,
                BaseUrl = resolved.BaseUrl,
                UserAgentExtension = resolved.UserAgentExtension,
                LoggerFactory = provider.GetService<ILoggerFactory>(),
                Handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
                                  .CreateHandler(HttpClientName),
                // The factory pools handlers across clients and rotates them on its own schedule;
                // disposing one out from under it would break every other client sharing it.
                DisposesHandler = false,
            };
        });

        return services;
    }
```

`AddDatabentoReference` is four lines, and they are the spec's §1 promise:

```csharp
    public static IServiceCollection AddDatabentoReference(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registers the transport itself if AddDatabentoHistorical has not already done so, and
        // reuses it if it has — TryAddSingleton is what makes both orders equivalent. One
        // HistoricalClient, one HttpClient, one connection pool to hist.databento.com.
        //
        // ReferenceClient(HistoricalClient) does not dispose the transport it was handed, and the
        // container disposes the HistoricalClient singleton directly, so nothing is disposed twice.
        AddDatabentoHistorical(services);
        services.TryAddSingleton(provider =>
            new ReferenceClient(provider.GetRequiredService<HistoricalClient>()));

        return services;
    }
```

`AddDatabentoLive`, the three overloads collapsing onto one body:

```csharp
    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services) =>
        AddDatabentoLive(services, DatabentoLiveBuilder.DefaultSessionName);

    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name, Action<LiveSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = AddDatabentoLive(services, name);
        builder.Services.Configure(name, configure);
        return builder;
    }

    public static DatabentoLiveBuilder AddDatabentoLive(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = SectionPathFor(services);

        services.AddOptions<LiveSessionOptions>(name)
                .BindConfiguration($"{path}:Live:{name}")
                .ValidateOnStart();

        // One validator per session, each skipping the names it does not own. Enumerable rather
        // than TryAddSingleton: two sessions need two of these, and TryAdd would register one.
        services.AddSingleton<IValidateOptions<LiveSessionOptions>>(provider =>
            new LiveSessionValidator(name, provider.GetRequiredService<IOptions<DatabentoOptions>>()));

        // Task 9 adds the keyed LiveSessionRunner and the IHostedService here.
        return new DatabentoLiveBuilder(services, name);
    }
```

**Sessions are declared in code and never conjured from configuration keys.** There is no scan of
`Databento:Live`'s children anywhere in this file, and there must not be one: a session that exists
because somebody added a JSON key, with no handler registered anywhere, fails at startup with a
cause that reads like a bug in this package.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests`

Expected: PASS.

- [ ] **Step 9: Update the baseline, then run the whole suite**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS, 0 warnings. Add every new public member to
`src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt` until RS0016 is quiet.

- [ ] **Step 10: Commit**

```bash
git add src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "feat(extensions): registration and startup validation

AddDatabento / AddDatabentoHistorical / AddDatabentoReference / AddDatabentoLive,
in namespace Microsoft.Extensions.DependencyInjection so they appear on
IServiceCollection with no extra using.

Binding is by path rather than by a carried IConfiguration: AddDatabentoHistorical()
takes no arguments and runs at registration time, so it has no provider to resolve
one from. AddDatabento stashes the path; BindConfiguration resolves the
IConfiguration itself when the options are built.

TryAddSingleton throughout, so AddDatabentoHistorical and AddDatabentoReference
compose in either order onto one HistoricalClient and one connection pool — which
is what ReferenceClient(HistoricalClient) was written for.

The registered HistoricalClient takes its handler from IHttpMessageHandlerFactory
with DisposesHandler=false, which is #<seam>'s seam used the one way it is meant to
be used.

Sessions are declared in code and never conjured from configuration keys.

Refs #<registration>"
```

---

## Task 6: `ReconnectSupervisor` — the backoff, with nothing else in it

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/ReconnectSupervisor.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/ReconnectSupervisorTests.cs`

**Interfaces:**
- Consumes: `ResolvedReconnect` (Task 4).
- Produces, and Tasks 7, 8 and 10 use exactly this:
  ```csharp
  public sealed class ReconnectSupervisor
  {
      public ReconnectSupervisor(ResolvedReconnect policy);
      public ResolvedReconnect Policy { get; }
      public int ConsecutiveFailures { get; }
      public Func<double> Jitter { get; init; }                                    // default Random.Shared.NextDouble
      public Func<Duration, CancellationToken, Task> Delay { get; init; }          // default Task.Delay
      public bool TryNextDelay(out Duration delay);
      public void RecordSuccess();
  }
  ```

**It goes first because it is pure arithmetic with two injected seams**, so it is settled without a
socket, without a host and without wall-clock waiting — and the runner that uses it (Task 7) then
has nothing left to prove about backoff. This is CLAUDE.md's `LatencyMeasurement` split applied
before the thing that would otherwise be hard to test exists.

**The two seams are `init` properties, not constructor parameters and not interfaces.** A
`Func<double>` and a `Func<Duration, CancellationToken, Task>` cost no public types and read as
what they are; an `IJitterSource` would be a public interface with one implementation and one test
double. Both have working defaults, so ordinary construction is `new ReconnectSupervisor(policy)`.

- [ ] **Step 1: Write the failing tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/ReconnectSupervisorTests.cs`:

```csharp
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The backoff policy, settled without a socket and without waiting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Jitter is injected, so the arithmetic has an answer known before the test runs.</b> The
/// production default is <see cref="Random.Shared"/>, which is exactly what makes a fleet of
/// restarted workers stop reconnecting in lockstep and exactly what makes an assertion about a
/// delay impossible. A <see cref="Func{TResult}"/> returning a fixed value turns the whole schedule
/// into something a table can state.
/// </para>
/// <para>
/// <b>Jitter is applied and is not configurable</b>, which is why there is no option for it and no
/// test that it can be turned off. Its only purpose is to stop a restarted fleet reconnecting
/// together, and a knob for that is a knob whose correct value is never anything but "on".
/// </para>
/// </remarks>
public class ReconnectSupervisorTests
{
    private static ResolvedReconnect Policy(int maxAttempts = 10, int initialSeconds = 1, int maxSeconds = 30) => new()
    {
        Enabled = true,
        InitialDelay = Duration.FromSeconds(initialSeconds),
        MaxDelay = Duration.FromSeconds(maxSeconds),
        MaxAttempts = maxAttempts,
    };

    /// <summary>Jitter at its ceiling, so the delay is the un-jittered base and the doubling shows.</summary>
    private static ReconnectSupervisor Supervisor(ResolvedReconnect policy) =>
        new(policy) { Jitter = () => 1.0 };

    [Fact]
    public void TryNextDelay_DoublesFromTheInitialDelayToTheCeiling()
    {
        var supervisor = Supervisor(Policy());

        var delays = new List<Duration>();
        while (supervisor.TryNextDelay(out var delay))
        {
            delays.Add(delay);
        }

        Assert.Equal(
            [
                Duration.FromSeconds(1),  Duration.FromSeconds(2),  Duration.FromSeconds(4),
                Duration.FromSeconds(8),  Duration.FromSeconds(16), Duration.FromSeconds(30),
                Duration.FromSeconds(30), Duration.FromSeconds(30), Duration.FromSeconds(30),
                Duration.FromSeconds(30),
            ],
            delays);
    }

    [Fact]
    public void TryNextDelay_StopsAfterMaxAttempts()
    {
        var supervisor = Supervisor(Policy(maxAttempts: 3));

        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.False(supervisor.TryNextDelay(out var exhausted));
        Assert.Equal(Duration.Zero, exhausted);
        Assert.Equal(3, supervisor.ConsecutiveFailures);
    }

    [Fact]
    public void RecordSuccess_ResetsTheCounterSoAFlappingGatewayReconnectsIndefinitely()
    {
        // MaxAttempts bounds *consecutive* failures. A gateway that drops every ten minutes and
        // reconnects each time is a gateway this keeps serving — the alternative silently stops a
        // worker overnight. Every reconnect is a newly billed session, which is what MaxAttempts
        // is really bounding.
        var supervisor = Supervisor(Policy(maxAttempts: 2));

        Assert.True(supervisor.TryNextDelay(out _));
        Assert.True(supervisor.TryNextDelay(out _));
        Assert.False(supervisor.TryNextDelay(out _));

        supervisor.RecordSuccess();

        Assert.Equal(0, supervisor.ConsecutiveFailures);
        Assert.True(supervisor.TryNextDelay(out var delay));
        // And the schedule restarts, rather than resuming at the ceiling.
        Assert.Equal(Duration.FromSeconds(1), delay);
    }

    [Fact]
    public void TryNextDelay_WhenReconnectionIsDisabled_IsFalseImmediately()
    {
        var supervisor = Supervisor(Policy() with { Enabled = false });

        Assert.False(supervisor.TryNextDelay(out _));
        Assert.Equal(0, supervisor.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(0.0, 500)]   // the floor: half the base
    [InlineData(0.5, 750)]
    [InlineData(1.0, 1000)]  // the ceiling: the base itself
    public void TryNextDelay_AppliesEqualJitterBetweenHalfTheBaseAndTheBase(double jitter, int expectedMilliseconds)
    {
        // Equal jitter rather than full jitter: a delay that can be arbitrarily close to zero
        // turns a bounded backoff into a tight retry loop against a gateway that is already
        // struggling, and every attempt is a billed session.
        var supervisor = new ReconnectSupervisor(Policy()) { Jitter = () => jitter };

        Assert.True(supervisor.TryNextDelay(out var delay));
        Assert.Equal(Duration.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Fact]
    public void TryNextDelay_WithAnInitialDelayPastTheCeiling_UsesTheCeiling()
    {
        // A misconfiguration the resolver already rejects, asserted here anyway: this type is
        // public and constructible directly, so it may not assume the resolver ran.
        var supervisor = Supervisor(Policy(initialSeconds: 60, maxSeconds: 30));

        Assert.True(supervisor.TryNextDelay(out var delay));
        Assert.Equal(Duration.FromSeconds(30), delay);
    }

    [Fact]
    public async Task Delay_DefaultsToARealWait_AndIsReplaceable()
    {
        var asked = new List<Duration>();
        var supervisor = new ReconnectSupervisor(Policy())
        {
            Jitter = () => 1.0,
            Delay = (delay, _) => { asked.Add(delay); return Task.CompletedTask; },
        };

        Assert.True(supervisor.TryNextDelay(out var first));
        await supervisor.Delay(first, TestContext.Current.CancellationToken);

        Assert.Equal([Duration.FromSeconds(1)], asked);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~ReconnectSupervisorTests"`

Expected: FAIL — `ReconnectSupervisor` does not exist.

- [ ] **Step 3: Write `ReconnectSupervisor`**

```csharp
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// The reconnection schedule for one live session: exponential backoff with jitter, bounded by
/// consecutive failures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, given PORTING.md §4.</b> That file says twice that <c>reconnect</c>
/// and <c>resubscribe</c> are deliberately separate and are not to be fused into an
/// auto-reconnect. That is a rule about <c>LiveClient</c>, and <b>a hosted service is precisely
/// the caller it defers to</b>: the library still does not fuse them, and this type makes the
/// caller's decision once, explicitly, with a bound on it.
/// </para>
/// <para>
/// <b><see cref="ResolvedReconnect.MaxAttempts"/> bounds <em>consecutive</em> failures</b> and
/// <see cref="RecordSuccess"/> resets the counter, so a gateway that flaps every ten minutes
/// reconnects indefinitely. That is deliberate — the alternative silently stops a worker
/// overnight. <b>Every successful reconnect starts a newly billed session</b>, so a reconnect
/// storm is a billing event and not merely a connection event; the bound is what caps it.
/// </para>
/// <para>
/// <b>Equal jitter, and it is not configurable.</b> Each delay is uniform between half the base
/// and the base. Full jitter — uniform between zero and the base — turns a bounded backoff into a
/// tight retry loop against a gateway that is already struggling, and each attempt costs money.
/// The purpose of any jitter here is to stop a restarted fleet reconnecting in lockstep, and a
/// knob for that is a knob whose correct value is never anything but "on".
/// </para>
/// </remarks>
public sealed class ReconnectSupervisor
{
    private readonly ResolvedReconnect _policy;
    private int _consecutiveFailures;

    /// <summary>Creates a supervisor for one policy.</summary>
    public ReconnectSupervisor(ResolvedReconnect policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>The policy this supervisor enforces.</summary>
    public ResolvedReconnect Policy => _policy;

    /// <summary>How many attempts have been handed out since the last <see cref="RecordSuccess"/>.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Supplies the jitter factor, in <c>[0, 1)</c>. Defaults to <see cref="Random.Shared"/>.
    /// </summary>
    /// <remarks>
    /// A seam so a test can state the schedule, not a knob: nothing in the options model reaches
    /// this, and nothing should.
    /// </remarks>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>Waits out a delay. Defaults to a real wait.</summary>
    /// <remarks>
    /// The same kind of seam as <see cref="Jitter"/>, and it is what lets
    /// <c>LiveSessionReconnectTests</c> assert a thirty-second backoff without taking thirty
    /// seconds. <c>Duration.ToTimeSpan()</c> rather than the banned type by name.
    /// </remarks>
    public Func<Duration, CancellationToken, Task> Delay { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay.ToTimeSpan(), cancellationToken);

    /// <summary>
    /// Takes the next delay, or reports that the policy is exhausted or disabled.
    /// </summary>
    /// <param name="delay">The delay to wait before the next attempt, or zero on <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when another attempt is allowed.</returns>
    public bool TryNextDelay(out Duration delay)
    {
        if (!_policy.Enabled || _consecutiveFailures >= _policy.MaxAttempts)
        {
            delay = Duration.Zero;
            return false;
        }

        _consecutiveFailures++;

        var expected = BaseDelay(_consecutiveFailures).ToInt64Nanoseconds();
        var half = expected / 2;

        delay = Duration.FromNanoseconds(half + (long)(half * Jitter()));
        return true;
    }

    /// <summary>Records that a session started, resetting the consecutive-failure count.</summary>
    public void RecordSuccess() => _consecutiveFailures = 0;

    /// <summary>
    /// The un-jittered delay for a one-based attempt number: the initial delay doubled once per
    /// previous attempt, capped at the ceiling.
    /// </summary>
    /// <remarks>
    /// Written as a loop rather than as <c>initial &lt;&lt; (attempt - 1)</c> because the shift
    /// overflows long before the <c>Math.Min</c> that would have capped it, and an overflowed
    /// backoff is a negative delay.
    /// </remarks>
    private Duration BaseDelay(int attempt)
    {
        var ceiling = _policy.MaxDelay.ToInt64Nanoseconds();
        var scaled = _policy.InitialDelay.ToInt64Nanoseconds();

        for (var i = 1; i < attempt && scaled < ceiling; i++)
        {
            scaled = scaled > ceiling / 2 ? ceiling : scaled * 2;
        }

        return Duration.FromNanoseconds(Math.Min(scaled, ceiling));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~ReconnectSupervisorTests"`

Expected: PASS, 9 tests.

- [ ] **Step 5: Update the baseline and build**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS after the new public members are added to `PublicAPI.Unshipped.txt`.

- [ ] **Step 6: Commit**

```bash
git add src/DatabentoDotNet.Extensions.Hosting/ReconnectSupervisor.cs \
        src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt \
        tests/DatabentoDotNet.Extensions.Hosting.Tests/ReconnectSupervisorTests.cs
git commit -m "feat(extensions): the reconnect backoff, with nothing else in it

Pure arithmetic behind two injected seams — jitter and the wait — so the whole
schedule is settled without a socket, without a host and without waiting. The
runner that uses it then has nothing left to prove about backoff.

MaxAttempts bounds consecutive failures and RecordSuccess resets the counter, so
a gateway that flaps every ten minutes reconnects indefinitely; the alternative
silently stops a worker overnight. Every successful reconnect is a newly billed
session, which is what the bound is really bounding.

Equal jitter rather than full jitter: a delay that can be arbitrarily close to
zero turns a bounded backoff into a tight retry loop against a gateway that is
already struggling, and every attempt costs money.

PORTING.md §4 forbids fusing reconnect and resubscribe inside LiveClient. That
rule defers to the caller, and a hosted service is the caller.

Refs #<reconnect>"
```

---

## Task 7: `ILiveRecordHandler` and `LiveSessionRunner` — the loop

**Files:**
- Modify: `src/DatabentoDotNet.Extensions.Hosting/ILiveRecordHandler.cs` (Task 5 wrote the placeholder)
- Create: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionState.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/Internal/ExtensionsLog.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/RecordingHandler.cs`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionRunnerTests.cs`

**Interfaces:**
- Consumes: `ResolvedLiveSession`, `ReconnectSupervisor`, `LiveClient`, `MockLiveGateway`.
- Produces:
  ```csharp
  public interface ILiveRecordHandler
  {
      void OnRecord(scoped RecordRef record);
      ValueTask OnFlushAsync(CancellationToken cancellationToken);
  }

  public enum LiveSessionState { NotStarted, Starting, Running, Reconnecting, Stopped, Faulted }

  public sealed class LiveSessionRunner : IAsyncDisposable
  {
      public LiveSessionRunner(ResolvedLiveSession session, ILiveRecordHandler handler,
                               ReconnectSupervisor supervisor, ILogger<LiveSessionRunner>? logger = null);
      public ResolvedLiveSession Session { get; }
      public LiveSessionState State { get; }
      public Exception? Fault { get; }
      public Metadata? Metadata { get; }
      public long RecordsReceived { get; }
      public Duration CloseTimeout { get; init; }          // default PT5S
      public Task StartSessionAsync(CancellationToken cancellationToken);
      public Task RunAsync(CancellationToken cancellationToken);
      public ValueTask DisposeAsync();
  }
  ```
  Task 8 adds recovery inside `RunAsync`; Task 9 registers the runner as a keyed singleton and
  wraps it in a `BackgroundService`; Task 10 adds a fifth, optional `LiveSessionMetrics?`
  constructor parameter and reads `State` for the health check.

**The split that makes this testable is the whole point of the task.** `RunAsync` takes a
`ResolvedLiveSession` and an `ILiveRecordHandler` and needs no host and no container, so
`MockLiveGateway` drives every one of these tests on every `dotnet test`. That is CLAUDE.md's
`LatencyMeasurement` doctrine — *"the expensive run is for the fact only it can settle, never for
finding out whether the code works"* — applied before there is anything expensive to run.

**Startup is separate from the loop, and that is what makes a wrong key fail the boot.**
`BackgroundService.StartAsync` awaits `ExecuteAsync` only until its first yield, so a session
started inside `ExecuteAsync` would fail in the background with the host already up. Connect,
authenticate, subscribe and start therefore live in `StartSessionAsync`, which Task 9's service
calls from its `StartAsync` override.

- [ ] **Step 1: Write the handler double**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/RecordingHandler.cs`:

```csharp
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Records what it was handed, in order, so a test can assert both the records and where the
/// flushes fell between them.
/// </summary>
/// <remarks>
/// <b>It copies the sequence number out of the record and keeps nothing else.</b> A
/// <see cref="RecordRef"/> is valid for the duration of the <see cref="OnRecord"/> call and no
/// longer — it points into the decoder's buffer, which the next fill may shift. A handler that
/// stored one would be the exact mistake this contract's <c>scoped</c> keyword exists to make a
/// compile error.
/// </remarks>
internal sealed class RecordingHandler : ILiveRecordHandler
{
    private readonly List<string> _events = [];

    /// <summary>Each record and each flush, interleaved in the order they happened.</summary>
    public IReadOnlyList<string> Events => _events;

    /// <summary>The sequence numbers seen, in order.</summary>
    public List<uint> Sequences { get; } = [];

    /// <summary>How many times <see cref="OnFlushAsync"/> was called.</summary>
    public int Flushes { get; private set; }

    /// <summary>Thrown from the next <see cref="OnRecord"/> when set.</summary>
    public Exception? ThrowOnRecord { get; set; }

    /// <summary>Thrown from the next <see cref="OnFlushAsync"/> when set.</summary>
    public Exception? ThrowOnFlush { get; set; }

    public void OnRecord(scoped RecordRef record)
    {
        if (ThrowOnRecord is { } fault)
        {
            throw fault;
        }

        if (record.TryGet<MboMsg>(out var mbo))
        {
            Sequences.Add(mbo.Sequence);
            _events.Add($"record:{mbo.Sequence}");
        }
        else
        {
            _events.Add("record:other");
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken)
    {
        Flushes++;
        _events.Add("flush");

        return ThrowOnFlush is { } fault
            ? ValueTask.FromException(fault)
            : ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Write the failing runner tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionRunnerTests.cs`:

```csharp
using System.Collections.Immutable;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The session loop, driven directly by <see cref="MockLiveGateway"/> with no host and no
/// container.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mock cannot confirm what it shares an author with</b>, and that limit is unchanged here
/// — but it also does not grow. This package adds no new reading of <c>live/protocol.rs</c>; it
/// composes calls whose protocol correctness <see cref="MockLiveGateway"/> and
/// <c>RealGatewaySessionTests</c> already established between them. Nothing in this file needs a
/// real gateway, and adding one would spend money to learn nothing new.
/// </para>
/// </remarks>
public class LiveSessionRunnerTests
{
    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ResolvedLiveSession Session(MockLiveGateway gateway) => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        Subscriptions = [new Subscription { Schema = Schema.Mbo, Symbols = Symbols.From(["AAPL"]) }],
        // Off, so this file's failures are the loop's rather than the backoff's.
        // LiveSessionReconnectTests turns it on.
        Reconnect = ResolvedReconnect.Default with { Enabled = false },
    };

    private static LiveSessionRunner Runner(MockLiveGateway gateway, ILiveRecordHandler handler) =>
        new(Session(gateway), handler, new ReconnectSupervisor(ResolvedReconnect.Default with { Enabled = false }));

    /// <summary>Runs the gateway's side of connect, authenticate, subscribe and start.</summary>
    private static async Task ServeStartupAsync(MockLiveGateway gateway)
    {
        await gateway.AuthenticateAsync(cancellationToken: Cancel);
        await gateway.ExpectSubscribeAsync(
            new ExpectedSubscription { Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"] },
            isLast: true,
            Cancel);
        await gateway.StartAsync(Cancel);
    }

    [Fact]
    public async Task StartSessionAsync_CompletesTheWholeHandshake()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        Assert.Equal(LiveSessionState.Running, runner.State);
        Assert.Null(runner.Fault);
        Assert.Equal(DatasetName, runner.Metadata!.Dataset);
    }

    [Fact]
    public async Task RunAsync_DrainsEveryRecordInOrder_AndFlushesOncePerFill()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        for (var sequence = 1u; sequence <= 3u; sequence++)
        {
            await gateway.SendRecordAsync(SyntheticMbo.Record(sequence), Cancel);
        }

        await gateway.CloseAsync();
        await runner.RunAsync(Cancel);

        Assert.Equal([1u, 2u, 3u], handler.Sequences);

        // The loop drains before it fills, so the first flush precedes every record: on the first
        // pass there is nothing buffered yet. That ordering is load-bearing rather than incidental
        // — a fill may shift the buffer, which is what invalidates a RecordRef, so the inner drain
        // must run to completion before each refill.
        Assert.Equal("flush", handler.Events[0]);
        Assert.Equal(["record:1", "record:2", "record:3"], handler.Events.Skip(1).Take(3));

        // And nothing is left behind at the tail: records read by the fill in pass N are drained
        // at the top of pass N + 1, before the fill that returns zero.
        Assert.Equal(3, handler.Sequences.Count);
        Assert.Equal(3, runner.RecordsReceived);
    }

    [Fact]
    public async Task RunAsync_WhenTheGatewayClosesCleanly_Stops()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.CloseAsync();

        await runner.RunAsync(Cancel);

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsWithoutFaulting()
    {
        // Shutdown is not a fault. A host stopping must not log a session as having failed.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var running = runner.RunAsync(stopping.Token);
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        await stopping.CancelAsync();
        await running;

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }

    [Fact]
    public async Task RunAsync_WhenTheHandlerThrows_IsFatalToTheSession()
    {
        // Swallowing it loses market data invisibly, which is the failure class this codebase
        // exists to convert into loud ones. A handler that wants to continue catches its own.
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler { ThrowOnRecord = new InvalidOperationException("boom") };
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;
        await gateway.SendRecordAsync(SyntheticMbo.Record(1), Cancel);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));

        Assert.Equal("boom", thrown.Message);
        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.Same(thrown, runner.Fault);
    }

    [Fact]
    public async Task RunAsync_WhenTheFlushThrows_IsAlsoFatal()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler { ThrowOnFlush = new InvalidOperationException("flush failed") };
        await using var runner = Runner(gateway, handler);

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.Faulted, runner.State);
    }

    [Fact]
    public async Task StartSessionAsync_WithAKeyTheGatewayRejects_Faults()
    {
        await using var gateway = new MockLiveGateway(DatasetName)
        {
            ExpectedApiKey = "a-different-32-character-api-key",
        };

        var handler = new RecordingHandler();
        await using var runner = Runner(gateway, handler);

        var serving = gateway.AuthenticateAsync(cancellationToken: Cancel);

        await Assert.ThrowsAsync<DatabentoAuthenticationException>(() => runner.StartSessionAsync(Cancel));

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.IsType<DatabentoAuthenticationException>(runner.Fault);
        await Assert.ThrowsAnyAsync<Exception>(() => serving);
    }

    [Fact]
    public async Task RunAsync_BeforeStartSessionAsync_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = Runner(gateway, new RecordingHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.NotStarted, runner.State);
    }

    [Fact]
    public async Task StartSessionAsync_Twice_Throws()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = Runner(gateway, new RecordingHandler());

        var serving = ServeStartupAsync(gateway);
        await runner.StartSessionAsync(Cancel);
        await serving;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartSessionAsync(Cancel));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~LiveSessionRunnerTests"`

Expected: FAIL — `LiveSessionRunner` and `LiveSessionState` do not exist.

- [ ] **Step 4: Document `ILiveRecordHandler` properly**

Replace Task 5's placeholder:

```csharp
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Receives the records a live session decodes. Registered with
/// <see cref="DatabentoLiveBuilder.AddRecordHandler{THandler}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two methods, because there is no third option.</b> An <c>async</c> method cannot take a
/// <c>ref struct</c> across an <c>await</c> — CS4007 — so a record can only be handed over
/// synchronously. <see cref="OnRecord"/> is that hand-over and
/// <see cref="OnFlushAsync"/> is where the I/O goes.
/// </para>
/// <para>
/// <b>The alternative costs two allocations per record and was rejected for that.</b>
/// <c>LiveClient.RecordsAsync</c> yields an <c>OwnedRecord</c> and is public; a caller who wants
/// it needs no help from this package. What this package promises is the guarantee
/// <c>LiveAllocationTests</c> asserts, in the one package whose reason to exist is that
/// guarantee.
/// </para>
/// <para>
/// <b>Implementations are singletons.</b> A DI scope per record would allocate and defeat the
/// contract. A handler needing scoped services opens a scope inside <see cref="OnFlushAsync"/>,
/// which is where its I/O belongs anyway.
/// </para>
/// <para>
/// <b>An exception from either method ends the session.</b> Swallowing it would lose market data
/// invisibly, which is the failure class this codebase exists to convert into loud ones. A handler
/// that wants to carry on catches its own.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class TradePrinter : ILiveRecordHandler
/// {
///     private readonly List&lt;string&gt; _batch = [];
///
///     public void OnRecord(scoped RecordRef record)
///     {
///         // Copy out what you need. The RecordRef points into the decoder's buffer and is valid
///         // for this call only — the next fill may shift it.
///         if (record.TryGet(out TradeMsg trade))
///         {
///             _batch.Add($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
///         }
///     }
///
///     public async ValueTask OnFlushAsync(CancellationToken cancellationToken)
///     {
///         if (_batch.Count == 0)
///         {
///             return;   // an already-completed ValueTask allocates nothing
///         }
///
///         await WriteAsync(_batch, cancellationToken);
///         _batch.Clear();
///     }
/// }
/// </code>
/// </example>
public interface ILiveRecordHandler
{
    /// <summary>
    /// Called once per record, inside the drain. <b>The record is valid for this call only.</b>
    /// </summary>
    /// <param name="record">
    /// The record, reinterpreted in place over the decoder's buffer. Copy out what you need; do
    /// not keep the reference.
    /// </param>
    void OnRecord(scoped RecordRef record);

    /// <summary>
    /// Called once per socket fill, after every buffered record has been drained. Where I/O goes.
    /// </summary>
    /// <remarks>
    /// Awaiting an already-completed <see cref="ValueTask"/> allocates nothing, so a handler with
    /// nothing to flush costs nothing.
    /// </remarks>
    /// <param name="cancellationToken">Cancelled when the session is stopping.</param>
    ValueTask OnFlushAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Write `LiveSessionState`**

```csharp
namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Where a live session is in its lifecycle. Read by the health check.</summary>
public enum LiveSessionState
{
    /// <summary>Constructed; <c>StartSessionAsync</c> has not been called.</summary>
    NotStarted = 0,

    /// <summary>Connecting, authenticating, subscribing, or starting.</summary>
    Starting = 1,

    /// <summary>Started, and reading records.</summary>
    Running = 2,

    /// <summary>The connection dropped and the backoff is running.</summary>
    Reconnecting = 3,

    /// <summary>The stream ended or the session was cancelled. Not a failure.</summary>
    Stopped = 4,

    /// <summary>The session failed. <c>LiveSessionRunner.Fault</c> says how.</summary>
    Faulted = 5,
}
```

- [ ] **Step 6: Write `Internal/ExtensionsLog.cs`**

Mirrors `Historical/Internal/HistoricalLog.cs` — `[LoggerMessage]` partials, stable event ids, and
PORTING.md §2's rule that this library logs only what the caller cannot otherwise see.

```csharp
using Microsoft.Extensions.Logging;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Internal;

/// <summary>Source-generated log messages for the hosted live session.</summary>
/// <remarks>
/// <para>
/// <b>Event ids are stable identifiers and are not to be renumbered.</b> A caller can filter on
/// one; changing it out from under them silently breaks that, in a way no compiler catches. Add a
/// new id for a new message rather than reusing or shifting one of these.
/// </para>
/// <para>
/// <b>Nothing here is per record, and that is both PORTING.md §2's rule and the allocation
/// guarantee agreeing.</b> The rule is that this library logs only what the caller cannot
/// otherwise see — and a caller sees every record, because they are handed each one. What they
/// cannot see is a reconnect: it happens between their calls, and without these lines a session
/// that dropped and recovered at 03:00 is indistinguishable from one that never dropped.
/// </para>
/// </remarks>
internal static partial class ExtensionsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Live session '{Session}' started on {Dataset} with {Subscriptions} subscription(s).")]
    public static partial void SessionStarted(ILogger logger, string session, string dataset, int subscriptions);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Live session '{Session}' dropped; reconnect attempt {Attempt} of {MaxAttempts} in {Delay}.")]
    public static partial void ReconnectAttempted(ILogger logger, string session, int attempt, int maxAttempts, Duration delay, Exception cause);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Live session '{Session}' reconnected after {Attempt} attempt(s). This is a newly billed session.")]
    public static partial void ReconnectSucceeded(ILogger logger, string session, int attempt);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Live session '{Session}' gave up after {Attempt} consecutive failed reconnects.")]
    public static partial void ReconnectExhausted(ILogger logger, string session, int attempt, Exception cause);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Live session '{Session}' ended after {Records} record(s).")]
    public static partial void SessionEnded(ILogger logger, string session, long records);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Live session '{Session}' did not close within {Timeout}; the socket is being dropped instead.")]
    public static partial void CloseTimedOut(ILogger logger, string session, Duration timeout);
}
```

- [ ] **Step 7: Write `LiveSessionRunner`**

The loop is the spec's, unchanged, and it has been compiled against the real `LiveClient` under
`IsAotCompatible` with both analyzers on:

```csharp
    /// <summary>
    /// Drains everything buffered, flushes, then refills. <see langword="true"/> when the gateway
    /// closed the stream.
    /// </summary>
    /// <remarks>
    /// <b>Drain before fill, and the inner loop must run to <see langword="false"/> before each
    /// refill.</b> That is not a style preference: a refill may shift the decoder's buffer, which
    /// is what invalidates a <c>RecordRef</c> the handler is still holding. It is also why nothing
    /// here needs the "drain once more at the end" that a fill-first loop needs — records read by
    /// the fill in one pass are drained at the top of the next, before the fill that returns zero.
    /// </remarks>
    private async Task<bool> PumpAsync(LiveClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Drain(client);
            await _handler.OnFlushAsync(cancellationToken).ConfigureAwait(false);

            if (await client.FillBufferAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Hands every buffered record to the handler.</summary>
    /// <remarks>
    /// Non-<c>async</c> because a <c>RecordRef</c> cannot be in scope across an <c>await</c>, and
    /// free of delegates and closures because either would be a per-record allocation in the one
    /// loop that promises none. <c>ExtensionsAllocationTests</c> is what holds that.
    /// </remarks>
    private void Drain(LiveClient client)
    {
        var received = 0L;

        while (client.TryNextRecord(out var record))
        {
            _handler.OnRecord(record);
            received++;
        }

        // One field write per fill rather than one per record: the same number, for less.
        RecordsReceived += received;
    }
```

`StartSessionAsync` sets `State = Starting`, builds the `LiveClient` from `Session`, then
`ConnectAsync` → `AuthenticateAsync` → one `SubscribeAsync` per subscription in order →
`StartAsync`, keeping the `Metadata`. Any exception sets `State = Faulted` and `Fault`, then
rethrows. On success it calls `_supervisor.RecordSuccess()`, sets `State = Running` and logs
`SessionStarted`.

`RunAsync` guards that `StartSessionAsync` ran, then loops on `PumpAsync` until it returns
`true` or the token is cancelled. `OperationCanceledException` for *this* token is a stop, not a
fault. Anything else sets `State = Faulted` and `Fault` and rethrows. On the way out it half-closes
and sets `State = Stopped`.

The bounded close:

```csharp
    /// <summary>
    /// Half-closes, so the gateway gets to finish rather than having the socket dropped on it —
    /// but bounded, so a gateway that never answers cannot hold the host's shutdown open.
    /// </summary>
    /// <remarks>
    /// The losing task is left to complete on its own rather than cancelled. It holds a timer and
    /// nothing else, it finishes within <see cref="CloseTimeout"/>, and cancelling it would leave
    /// a faulted task nobody awaits — noise, in exchange for reclaiming one timer five seconds
    /// early.
    /// </remarks>
    private async Task CloseAsync(LiveClient client)
    {
        var closing = client.CloseAsync();
        var expiring = _supervisor.Delay(CloseTimeout, CancellationToken.None);

        if (await Task.WhenAny(closing, expiring).ConfigureAwait(false) == expiring)
        {
            ExtensionsLog.CloseTimedOut(_logger, Session.Name, CloseTimeout);
            return;   // DisposeAsync tears the socket down; the half-close was the courtesy.
        }

        await closing.ConfigureAwait(false);
    }
```

`DisposeAsync` disposes the `LiveClient` if one was built, and is idempotent.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~LiveSessionRunnerTests"`

Expected: PASS, 9 tests.

- [ ] **Step 9: Update the baseline and run the whole suite**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS, 0 warnings.

- [ ] **Step 10: Commit**

```bash
git add src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "feat(extensions): the dispatch contract and the session loop

ILiveRecordHandler is synchronous per record and asynchronous per fill, because
there is no third option: an async method cannot take a ref struct across an
await, so a record can only be handed over synchronously. OnFlushAsync is where
the I/O goes, and awaiting an already-completed ValueTask allocates nothing.

LiveSessionRunner takes a ResolvedLiveSession and an ILiveRecordHandler and needs
no host and no container, so MockLiveGateway drives every one of its tests on
every dotnet test. Startup is a separate method from the loop, which is what
lets a wrong key fail the host boot rather than a background task nobody watches.

A handler exception is fatal to the session. Swallowing it would lose market data
invisibly, which is the failure class this codebase exists to convert into loud
ones.

Refs #<runner>"
```

---

## Task 8: Recovery — reconnect, resubscribe, restart

**Files:**
- Modify: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs` (`RunAsync` gains a
  `catch`; two private methods added)
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionReconnectTests.cs`

**Interfaces:**
- Consumes: `ReconnectSupervisor` (Task 6), `LiveSessionRunner` (Task 7),
  `LiveClient.ReconnectAsync` / `ResubscribeAsync` / `StartAsync`.
- Produces: no new public members. `LiveSessionState.Reconnecting` starts being reported, which
  Task 10's health check reads.

**Three rules, and the tests below are each one of them.**

1. **The order is `ReconnectAsync` → `ResubscribeAsync` → `StartAsync`, and it is not
   interchangeable.** `ResubscribeAsync` clears each subscription's `Start`, so a reconnect does
   not replay the same intraday history twice — and the symptom of getting it wrong, duplicated
   records after a reconnect, looks like a gateway fault and is not one. PORTING.md:1256.
2. **A clean close is not a failure and does not reconnect.** `FillBufferAsync` returning `0` is
   how a session ends — a completed replay, or a gateway shutting down deliberately. Only an
   *exception* enters the backoff. Getting this wrong turns every orderly end into a reconnect
   storm, and every reconnect is a billed session.
3. **`DatabentoAuthenticationException` is fatal wherever it appears.** Retrying a wrong key bills
   nothing and fixes nothing. Every other `LiveException`, plus `IOException` and
   `SocketException`, is transient.

The classification is written as an explicit list rather than as `is not
DatabentoAuthenticationException`, because a negation silently classifies every exception type
added later as transient — including one that means "stop".

```csharp
    /// <summary>Whether a failure is worth reconnecting for.</summary>
    /// <remarks>
    /// <para>
    /// An explicit list, not <c>is not DatabentoAuthenticationException</c>. A negation classifies
    /// every exception type added later as transient by default, including one that means "stop",
    /// and nothing would say so.
    /// </para>
    /// <para>
    /// <see cref="ConnectTimeoutException"/> needs no arm of its own: it derives from
    /// <see cref="LiveConnectException"/>, which is already here.
    /// </para>
    /// </remarks>
    private static bool IsTransient(Exception exception) => exception switch
    {
        // Retrying a wrong key bills nothing and fixes nothing.
        DatabentoAuthenticationException => false,
        LiveConnectException => true,
        AuthTimeoutException => true,
        HeartbeatTimeoutException => true,
        LiveProtocolException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ => false,
    };
```

- [ ] **Step 1: Write the failing reconnect tests**

`tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionReconnectTests.cs`:

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Publishers;
using DatabentoDotNet.Live;
using DatabentoDotNet.Live.Tests;
using NodaTime;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Reconnection: the order it happens in, the schedule it happens on, and when it stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure is provoked with a read timeout, not with a dropped socket</b>, because a read
/// timeout is a failure the mock can produce by doing nothing at all — and because
/// <c>LiveClientReconnectTests</c> already establishes that a
/// <see cref="HeartbeatTimeoutException"/> leaves the client in exactly the state
/// <c>ReconnectAsync</c> is documented to recover from.
/// </para>
/// <para>
/// <b>No test here waits out a real backoff.</b> <see cref="ReconnectSupervisor.Delay"/> is
/// replaced with a recorder, so a thirty-second ceiling is asserted in microseconds. What that
/// costs is that these tests say nothing about whether <c>Task.Delay</c> works, which is not this
/// repository's question.
/// </para>
/// </remarks>
public class LiveSessionReconnectTests
{
    private const string SecondSessionId = "6";

    private static readonly string DatasetName = Dataset.XnasItch.ToWireString();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ResolvedLiveSession Session(MockLiveGateway gateway, ResolvedReconnect reconnect) => new()
    {
        Name = "equities",
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
        // Short enough that a silent gateway is a failure inside a test's patience.
        ReadTimeout = Duration.FromMilliseconds(250),
        Subscriptions =
        [
            new Subscription
            {
                Schema = Schema.Mbo,
                Symbols = Symbols.From(["AAPL"]),
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
        ],
        Reconnect = reconnect,
    };

    [Fact]
    public async Task RunAsync_AfterATransientFailure_ReconnectsResubscribesAndRestarts()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new RecordingHandler();
        var delays = new List<Duration>();
        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Jitter = () => 1.0,
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), handler, supervisor);

        // First session, with a replay start.
        var first = gateway.AuthenticateAsync(cancellationToken: Cancel);
        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
            isLast: true,
            Cancel);
        var serving = gateway.StartAsync(Cancel);

        await runner.StartSessionAsync(Cancel);
        await first;
        await subscribing;
        await serving;

        var running = runner.RunAsync(Cancel);

        // The gateway now says nothing, so the client's 250 ms read budget expires and the runner
        // enters the backoff. Its side of the socket is closed so the second handshake can be
        // accepted.
        await gateway.CloseAsync();

        // The replayed subscription carries no Start. That is the whole point of ResubscribeAsync
        // and the reason the order is reconnect, resubscribe, start — a reconnect that replayed
        // the original subscription verbatim would ask for the same intraday history twice.
        var rehandshake = gateway.AuthenticateAsync(SecondSessionId, cancellationToken: Cancel);
        var replay = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo,
                StypeIn = SType.RawSymbol,
                Symbols = ["AAPL"],
                Start = null,
            },
            isLast: true,
            Cancel);
        var reserving = gateway.StartAsync(Cancel);

        await rehandshake;
        await replay;
        await reserving;

        await gateway.SendRecordAsync(SyntheticMbo.Record(7), Cancel);
        await gateway.CloseAsync();
        await running;

        Assert.Equal([7u], handler.Sequences);
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Equal([Duration.FromSeconds(1)], delays);
        // The counter reset when the session restarted.
        Assert.Equal(0, supervisor.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunAsync_WhenEveryAttemptFails_GivesUpAfterMaxAttemptsAndRethrows()
    {
        var policy = ResolvedReconnect.Default with { MaxAttempts = 3 };
        var delays = new List<Duration>();

        await using var gateway = new MockLiveGateway(DatasetName);
        var supervisor = new ReconnectSupervisor(policy)
        {
            Jitter = () => 1.0,
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, policy), new RecordingHandler(), supervisor);

        var first = gateway.AuthenticateAsync(cancellationToken: Cancel);
        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"],
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
            isLast: true, Cancel);
        var serving = gateway.StartAsync(Cancel);

        await runner.StartSessionAsync(Cancel);
        await first;
        await subscribing;
        await serving;

        var running = runner.RunAsync(Cancel);

        // The listener stops accepting, so every reconnect fails.
        await gateway.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => running);

        Assert.Equal(LiveSessionState.Faulted, runner.State);
        Assert.NotNull(runner.Fault);
        Assert.Equal(3, delays.Count);
        Assert.Equal(
            [Duration.FromSeconds(1), Duration.FromSeconds(2), Duration.FromSeconds(4)],
            delays);
    }

    [Fact]
    public async Task RunAsync_WithReconnectionDisabled_PropagatesTheFailureImmediately()
    {
        var policy = ResolvedReconnect.Default with { Enabled = false };

        await using var gateway = new MockLiveGateway(DatasetName);
        await using var runner = new LiveSessionRunner(
            Session(gateway, policy), new RecordingHandler(), new ReconnectSupervisor(policy));

        var first = gateway.AuthenticateAsync(cancellationToken: Cancel);
        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"],
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
            isLast: true, Cancel);
        var serving = gateway.StartAsync(Cancel);

        await runner.StartSessionAsync(Cancel);
        await first;
        await subscribing;
        await serving;

        await Assert.ThrowsAsync<HeartbeatTimeoutException>(() => runner.RunAsync(Cancel));
        Assert.Equal(LiveSessionState.Faulted, runner.State);
    }

    [Fact]
    public async Task RunAsync_WhenTheGatewayClosesCleanly_DoesNotReconnect()
    {
        // A clean close is how a session ends — a completed replay, or a gateway shutting down
        // deliberately. Treating it as a failure would turn every orderly end into a reconnect
        // storm, and every reconnect is a newly billed session.
        var delays = new List<Duration>();

        await using var gateway = new MockLiveGateway(DatasetName);
        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), new RecordingHandler(), supervisor);

        var first = gateway.AuthenticateAsync(cancellationToken: Cancel);
        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"],
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
            isLast: true, Cancel);
        var serving = gateway.StartAsync(Cancel);

        await runner.StartSessionAsync(Cancel);
        await first;
        await subscribing;
        await serving;
        await gateway.CloseAsync();

        await runner.RunAsync(Cancel);

        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task RunAsync_DuringTheBackoff_StopsWhenCancelled()
    {
        var started = new TaskCompletionSource();

        await using var gateway = new MockLiveGateway(DatasetName);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        var supervisor = new ReconnectSupervisor(ResolvedReconnect.Default)
        {
            Delay = async (delay, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };

        await using var runner = new LiveSessionRunner(
            Session(gateway, ResolvedReconnect.Default), new RecordingHandler(), supervisor);

        var first = gateway.AuthenticateAsync(cancellationToken: Cancel);
        var subscribing = gateway.ExpectSubscribeAsync(
            new ExpectedSubscription
            {
                Schema = Schema.Mbo, StypeIn = SType.RawSymbol, Symbols = ["AAPL"],
                Start = Instant.FromUtc(2026, 8, 31, 13, 30),
            },
            isLast: true, Cancel);
        var serving = gateway.StartAsync(Cancel);

        await runner.StartSessionAsync(Cancel);
        await first;
        await subscribing;
        await serving;

        var running = runner.RunAsync(stopping.Token);
        await gateway.CloseAsync();
        await started.Task;

        await stopping.CancelAsync();
        await running;

        // Cancelled during a backoff is a shutdown, not a fault: the host is stopping.
        Assert.Equal(LiveSessionState.Stopped, runner.State);
        Assert.Null(runner.Fault);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~LiveSessionReconnectTests"`

Expected: FAIL — the runner has no recovery, so the first transient failure propagates out of
`RunAsync`.

- [ ] **Step 3: Add the `catch` to `RunAsync`**

```csharp
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await PumpAsync(client, cancellationToken).ConfigureAwait(false))
                {
                    // A clean close, which is how a session ends. Not a failure, and not something
                    // to reconnect from — see IsTransient's remarks.
                    break;
                }
            }
            catch (Exception exception)
                when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                if (!await TryRecoverAsync(client, exception, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
```

- [ ] **Step 4: Add `TryRecoverAsync` and `IsTransient`**

```csharp
    /// <summary>
    /// Runs the backoff until a session restarts or the policy is exhausted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reconnect, resubscribe, then start, and that order is not interchangeable.</b>
    /// <c>ResubscribeAsync</c> clears each subscription's <c>Start</c>, so a reconnect does not ask
    /// the gateway for the same intraday history a second time — and the symptom of getting it
    /// wrong, duplicated records after a reconnect, looks like a gateway fault and is not one.
    /// PORTING.md §4.
    /// </para>
    /// <para>
    /// <b>Every successful restart is a newly billed session</b>, which is why
    /// <see cref="ReconnectSupervisor"/> bounds the attempts and why the success is logged at
    /// information level rather than debug.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when a session is running again.</returns>
    private async Task<bool> TryRecoverAsync(LiveClient client, Exception cause, CancellationToken cancellationToken)
    {
        State = LiveSessionState.Reconnecting;

        while (_supervisor.TryNextDelay(out var delay))
        {
            ExtensionsLog.ReconnectAttempted(
                _logger, Session.Name, _supervisor.ConsecutiveFailures,
                _supervisor.Policy.MaxAttempts, delay, cause);

            await _supervisor.Delay(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                await client.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                await client.ResubscribeAsync(cancellationToken).ConfigureAwait(false);
                Metadata = await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                // The reason reported on the next attempt is the reason the *last* attempt failed,
                // not the one that started the backoff. A log that kept repeating the original
                // cause would hide a connection that started failing differently half way through.
                cause = exception;
                continue;
            }

            // Logged before the reset, so the message can say how many attempts it took.
            ExtensionsLog.ReconnectSucceeded(_logger, Session.Name, _supervisor.ConsecutiveFailures);
            _supervisor.RecordSuccess();
            State = LiveSessionState.Running;
            return true;
        }

        ExtensionsLog.ReconnectExhausted(_logger, Session.Name, _supervisor.ConsecutiveFailures, cause);
        return false;
    }
```

plus `IsTransient` exactly as given at the top of this task.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~LiveSessionReconnectTests"`

Expected: PASS, 5 tests.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"`

Expected: PASS, 0 warnings. No new public members, so the baseline is unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs \
        tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionReconnectTests.cs
git commit -m "feat(extensions): reconnect, resubscribe, restart, bounded

Reconnect then resubscribe then start, and that order is not interchangeable:
ResubscribeAsync clears each subscription's Start, so a reconnect does not ask
the gateway for the same intraday history twice. The symptom of getting it wrong
looks like a gateway fault and is not one.

A clean close is not a failure and does not reconnect. FillBufferAsync returning
zero is how a session ends; treating it as a failure would turn every orderly end
into a reconnect storm, and every reconnect is a billed session.

The transient/fatal split is an explicit list rather than a negation of
DatabentoAuthenticationException, because a negation classifies every exception
type added later as transient by default — including one that means stop.

Refs #<reconnect>"
```

---

## Task 9: `LiveSessionService`, and wiring a session into the container

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionService.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/ServiceCollectionExtensions.cs`
  (`AddDatabentoLive` gains two registrations and one private factory)
- Modify: `src/DatabentoDotNet.Extensions.Hosting/PublicAPI.Unshipped.txt`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/LiveSessionServiceTests.cs`

**Interfaces:**
- Consumes: `LiveSessionRunner`, `LiveSessionResolver`, the keyed `ILiveRecordHandler`.
- Produces:
  ```csharp
  public sealed class LiveSessionService : BackgroundService
  {
      public LiveSessionService(LiveSessionRunner runner);
      public LiveSessionRunner Runner { get; }
      public override Task StartAsync(CancellationToken cancellationToken);
      protected override Task ExecuteAsync(CancellationToken stoppingToken);
  }
  ```
  and, in the container: `keyed singleton LiveSessionRunner` under the session name, which Task 10's
  health check resolves.

**The service is thin by construction, and there is nothing in it worth a test of its own.** What
these tests cover is the *wiring*: that the container builds a runner from the resolved options,
that startup happens during the host's start, and that a wrong key stops the boot.

**`StartAsync` is overridden, and that is the load-bearing line.**
`BackgroundService.StartAsync` awaits `ExecuteAsync` only until its first yield, so a session
started inside `ExecuteAsync` would fail in the background with the host already up and serving
traffic it cannot fulfil. Calling `StartSessionAsync` before `base.StartAsync` makes connect,
authenticate, subscribe and start part of the host's own startup.

**Nothing calls `IHostApplicationLifetime`, and that is deliberate.**
`BackgroundServiceExceptionBehavior.StopHost` has been the default since .NET 6, so an exception
out of `ExecuteAsync` already stops the host. Adding an explicit `StopApplication()` would be a
second mechanism for the same outcome, differing only in the log line it produces.

- [ ] **Step 1: Write the failing tests**

`LiveSessionServiceTests` boots a real `HostApplicationBuilder` against `MockLiveGateway`:

| Test | Asserts |
|---|---|
| `StartAsync_ConnectsDuringHostStartup` | after `host.StartAsync()`, the keyed runner's `State` is `Running` — i.e. the session was established by the host's start rather than after it |
| `ExecuteAsync_DeliversRecordsToTheRegisteredHandler` | records sent after start reach the handler registered by `AddRecordHandler<T>()` |
| `StartAsync_WithAKeyTheGatewayRejects_FailsTheBoot` | `host.StartAsync()` throws `DatabentoAuthenticationException`, and the host does not come up |
| `StopAsync_ClosesTheSession` | after `host.StopAsync()`, `State` is `Stopped` and `Fault` is null |
| `TwoSessions_RunIndependently` | two `AddDatabentoLive` names against two mock gateways, two handlers, and each handler sees only its own records |

The gateway address reaches configuration through the lambda overload, which is the honest way to
say "a test needs a gateway a configuration file would never name":

```csharp
        builder.Services
            .AddDatabentoLive("equities", options => options.Gateway = gateway.Address.ToString())
            .AddRecordHandler(_ => handler);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~LiveSessionServiceTests"`

Expected: FAIL — `LiveSessionService` does not exist and no `IHostedService` is registered.

- [ ] **Step 3: Write `LiveSessionService`**

```csharp
using Microsoft.Extensions.Hosting;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Runs one live session for as long as the host is up.</summary>
/// <remarks>
/// <para>
/// <b>Thin by construction.</b> Everything worth testing is in <see cref="LiveSessionRunner"/>,
/// which takes a resolved session and a handler and needs no host and no container — so
/// <c>MockLiveGateway</c> drives it directly. What is left here is the two lines that make a
/// runner a hosted service, and they are the two lines below.
/// </para>
/// <para>
/// <b><see cref="StartAsync"/> is overridden so a bad session fails the host's boot.</b>
/// <see cref="BackgroundService.StartAsync"/> awaits <see cref="ExecuteAsync"/> only until its
/// first yield, so a session established inside <c>ExecuteAsync</c> would fail in the background
/// with the host already up and serving traffic it cannot fulfil. Connecting, authenticating,
/// subscribing and starting therefore happen here, before <c>base.StartAsync</c>.
/// </para>
/// <para>
/// <b>Nothing here calls <c>IHostApplicationLifetime</c>.</b>
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> has been the default since .NET 6,
/// so an exception out of <see cref="ExecuteAsync"/> — which is what a faulted handler becomes —
/// already stops the host. A second mechanism for the same outcome would differ only in its log
/// line.
/// </para>
/// </remarks>
public sealed class LiveSessionService : BackgroundService
{
    private readonly LiveSessionRunner _runner;

    /// <summary>Creates a hosted service around one runner.</summary>
    public LiveSessionService(LiveSessionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>The runner this service drives.</summary>
    public LiveSessionRunner Runner => _runner;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _runner.StartSessionAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _runner.RunAsync(stoppingToken);
}
```

- [ ] **Step 4: Register the runner and the service**

In `AddDatabentoLive(IServiceCollection, string)`, replacing the `// Task 9 adds …` comment:

```csharp
        // Keyed by session name, so two sessions in one host are two runners with two handlers and
        // two independent reconnect states. Also what LiveSessionHealthCheck resolves.
        services.AddKeyedSingleton(name, (provider, key) => CreateRunner(provider, (string)key!));

        // AddSingleton rather than AddHostedService: the latter is TryAddEnumerable on
        // IHostedService by implementation type, so a second session would silently not be
        // registered — both would be LiveSessionService.
        services.AddSingleton<IHostedService>(provider =>
            new LiveSessionService(provider.GetRequiredKeyedService<LiveSessionRunner>(name)));
```

and the factory:

```csharp
    private static LiveSessionRunner CreateRunner(IServiceProvider provider, string name)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get(name);
        var root = provider.GetRequiredService<IOptions<DatabentoOptions>>().Value;

        var result = LiveSessionResolver.Resolve(
            name,
            options,
            root,
            Environment.GetEnvironmentVariable(LiveSessionResolver.ApiKeyEnvironmentVariable));

        if (!result.Succeeded)
        {
            // Unreachable when ValidateOnStart ran — and thrown anyway. This runner is resolvable
            // from the container directly, and "the validator will have caught it" is an
            // assumption about a caller rather than a property of this code.
            throw new OptionsValidationException(name, typeof(LiveSessionOptions), result.Failures);
        }

        return new LiveSessionRunner(
            result.Session,
            provider.GetRequiredKeyedService<ILiveRecordHandler>(name),
            new ReconnectSupervisor(result.Session.Reconnect),
            provider.GetService<ILogger<LiveSessionRunner>>());
    }
```

**`AddSingleton<IHostedService>` rather than `AddHostedService<T>()`** is not a stylistic choice:
`AddHostedService<T>` registers with `TryAddEnumerable`, which deduplicates by implementation type
— so a second `AddDatabentoLive` would silently register nothing and the second session would never
run. Verify it with the `TwoSessions_RunIndependently` test rather than trusting this paragraph.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests`

Expected: PASS.

- [ ] **Step 6: Update the baseline, build, commit**

```bash
dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"
git add src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "feat(extensions): run a live session as a hosted service

StartAsync is overridden so connect/authenticate/subscribe/start happen during
the host's own startup: BackgroundService.StartAsync awaits ExecuteAsync only
until its first yield, so a session established inside ExecuteAsync would fail
in the background with the host already up.

AddSingleton<IHostedService> rather than AddHostedService<T>, because the latter
deduplicates by implementation type — a second AddDatabentoLive would silently
register nothing and the second session would never run.

Nothing calls IHostApplicationLifetime: BackgroundServiceExceptionBehavior.StopHost
has been the default since .NET 6, and a second mechanism for the same outcome
would differ only in its log line.

Refs #<runner>"
```

---

## Task 10: Observability — metrics that cost nothing, and an opt-in health check

**Files:**
- Create: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionMetrics.cs`
- Create: `src/DatabentoDotNet.Extensions.Hosting/HealthChecks/LiveSessionHealthCheck.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/DatabentoLiveBuilder.cs` (`AddHealthCheck`)
- Modify: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs` (an optional metrics
  parameter, and four call sites)
- Modify: `src/DatabentoDotNet.Extensions.Hosting/ServiceCollectionExtensions.cs`
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/ObservabilityTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class LiveSessionMetrics : IDisposable
  {
      public const string MeterName = "DatabentoDotNet.Extensions.Hosting";
      public LiveSessionMetrics();
      public LiveSessionMetrics(IMeterFactory meterFactory);
      public void RecordsReceived(long count, in KeyValuePair<string, object?> session);
      public void SessionStarted(in KeyValuePair<string, object?> session);
      public void ReconnectAttempted(in KeyValuePair<string, object?> session);
      public void FlushCompleted(double milliseconds, in KeyValuePair<string, object?> session);
      public void Dispose();
  }

  public sealed class LiveSessionHealthCheck : IHealthCheck
  {
      public LiveSessionHealthCheck(LiveSessionRunner runner);
      public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken);
  }

  // on DatabentoLiveBuilder
  public DatabentoLiveBuilder AddHealthCheck(string? name = null, HealthStatus? failureStatus = null, IEnumerable<string>? tags = null);
  ```

**Two shapes here exist to keep the zero-per-record guarantee, and both look odd without the
reason.**

1. **The count is accumulated in a local and published once per flush.** A `Counter<long>.Add` per
   record is a per-record cost on the one path that promises none. A `long` increment in the drain
   and one `Add` after it reports the same number for nothing.
2. **The session tag is a pre-built `KeyValuePair<string, object?>` field on the runner, passed by
   `in`.** Building the tag at each call site would allocate — `TagList` and the `params` overloads
   both do — and a per-flush allocation would still fail `ExtensionsAllocationTests`, which
   measures the whole loop. A field built once in the runner's constructor, passed by readonly
   reference, costs nothing per call. A `string` in an `object?` does not box.

**The health check's state mapping is a decision, and here it is stated rather than implied:**

| `LiveSessionState` | Result | Why |
|---|---|---|
| `Running` | `Healthy` | Started and reading |
| `NotStarted`, `Starting` | `Degraded` | Coming up, not yet serving |
| `Reconnecting` | `Degraded` | The backoff is running; it may well recover |
| `Stopped` | `Unhealthy` | The stream ended and the worker is doing nothing. A deliberate shutdown makes this unreachable in practice, because the endpoint stops with the host |
| `Faulted` | `Unhealthy` | With `Fault`'s message as the description |

- [ ] **Step 1: Write the failing tests**

`ObservabilityTests` covers both halves:

| Test | Asserts |
|---|---|
| `Metrics_CountRecordsOncePerFlush_NotOncePerRecord` | a `MeterListener` sees measurements whose sum equals the record count, and whose *number* equals the flush count |
| `Metrics_TagEveryMeasurementWithTheSessionName` | each measurement carries `databento.session` = the session name |
| `Metrics_CountReconnectAttemptsAndSessionStarts` | one `session.started` per successful start, one `reconnect.attempted` per attempt |
| `HealthCheck_WhileRunning_IsHealthy` | `HealthStatus.Healthy` |
| `HealthCheck_WhileReconnecting_IsDegraded` | driven through the reconnect path from Task 8, with the injected `Delay` holding the runner in `Reconnecting` |
| `HealthCheck_AfterAFault_IsUnhealthyAndCarriesTheReason` | `HealthStatus.Unhealthy`, and `Description` contains the fault's message |
| `AddHealthCheck_RegistersUnderADefaultName` | `databento-live-equities` appears in `HealthCheckServiceOptions.Registrations` |
| `AddHealthCheck_WithTwoSessions_RegistersTwoChecks` | two registrations, two distinct names |

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~ObservabilityTests"`

Expected: FAIL — `LiveSessionMetrics` and `LiveSessionHealthCheck` do not exist.

- [ ] **Step 3: Write `LiveSessionMetrics`**

Four instruments on one `Meter` named `DatabentoDotNet.Extensions.Hosting`:

| Instrument | Kind | Unit |
|---|---|---|
| `databento.live.records.received` | `Counter<long>` | `{record}` |
| `databento.live.sessions.started` | `Counter<long>` | `{session}` |
| `databento.live.reconnects.attempted` | `Counter<long>` | `{attempt}` |
| `databento.live.flush.duration` | `Histogram<double>` | `ms` |

Every method takes the session tag by `in` and forwards it to the single-tag overload, which is the
one that does not allocate. Two constructors: the parameterless one owns its `Meter`, and the
`IMeterFactory` one takes the factory's, which is what a host supplies and what
`IDisposable` must then not dispose.

- [ ] **Step 4: Wire the metrics into the runner**

One optional constructor parameter, four call sites, and the pre-built tag:

```csharp
    private readonly KeyValuePair<string, object?> _sessionTag;
    // …
    _sessionTag = new KeyValuePair<string, object?>("databento.session", session.Name);
```

`Drain` publishes once, after the loop:

```csharp
        RecordsReceived += received;
        if (received > 0)
        {
            _metrics?.RecordsReceived(received, in _sessionTag);
        }
```

`StartSessionAsync` and `TryRecoverAsync` publish `SessionStarted` and `ReconnectAttempted`.
`PumpAsync` times the flush with `Stopwatch.GetTimestamp()` — a `long`, so no allocation and no
banned type — and publishes `FlushCompleted` only when metrics are configured.

- [ ] **Step 5: Write the health check and `AddHealthCheck`**

```csharp
    public DatabentoLiveBuilder AddHealthCheck(
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        var registrationName = name ?? $"databento-live-{Name}";

        Services.Configure<HealthCheckServiceOptions>(options =>
            options.Registrations.Add(new HealthCheckRegistration(
                registrationName,
                provider => new LiveSessionHealthCheck(
                    provider.GetRequiredKeyedService<LiveSessionRunner>(Name)),
                failureStatus,
                tags)));

        return this;
    }
```

Registered directly into `HealthCheckServiceOptions` rather than through `IHealthChecksBuilder`, so
the consumer's own `AddHealthChecks()` call is not a prerequisite and the two compose in either
order — the same property `TryAddSingleton` buys for the historical and reference clients.

- [ ] **Step 6: Register the metrics singleton**

In `AddDatabento`: `services.TryAddSingleton<LiveSessionMetrics>();` — using the `IMeterFactory`
constructor when one is registered, which it is in any host that called
`AddMetrics()`. `CreateRunner` passes `provider.GetService<LiveSessionMetrics>()`.

- [ ] **Step 7: Run the tests, update the baseline, commit**

```bash
dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests
dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"
git add src/DatabentoDotNet.Extensions.Hosting tests/DatabentoDotNet.Extensions.Hosting.Tests
git commit -m "feat(extensions): metrics that cost nothing, and an opt-in health check

Records are counted into a local and published once per flush, not once per
record: a Counter<long>.Add per record is a per-record cost on the one path that
promises none, and a long increment reports the same number for nothing.

The session tag is a pre-built KeyValuePair field passed by in, because building
one at the call site allocates and ExtensionsAllocationTests measures the whole
loop, flushes included.

The health check registers straight into HealthCheckServiceOptions rather than
through IHealthChecksBuilder, so a consumer's own AddHealthChecks() is not a
prerequisite and the two compose in either order.

Refs #<observability>"
```

---

## Task 11: `ExtensionsAllocationTests` — zero bytes per record, asserted

**Files:**
- Test: `tests/DatabentoDotNet.Extensions.Hosting.Tests/ExtensionsAllocationTests.cs`
- Modify: `src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs`, only if the measurement
  finds something

**Interfaces:**
- Consumes: `LiveSessionRunner`, `LiveSessionMetrics`, `MockLiveGateway`, `SyntheticMbo`.
- Produces: nothing public. It produces the guarantee.

**This is the package's reason to exist, asserted rather than asserted-to.** `LiveAllocationTests`
already holds `FillBufferAsync`/`TryNextRecord`; what this adds is everything the runner puts
around them — the handler dispatch, the flush, the metrics publish, and the `async` state machine
holding the loop. An `async` method is exactly where a per-call allocation hides, because a state
machine box, a `CancellationTokenSource` and a cancellation registration are all invisible in the
source.

**Four things are copied from `LiveAllocationTests` deliberately, and each is load-bearing:**

1. **A warm-up batch before the measured one**, on the same connection and the same decoder, so
   what is measured is steady state and not a second cold start wearing a warm-up's name.
2. **The measured batch is sized to fit the socket's receive buffer** — around 28 KB at
   `MboMsg.WireSize` — so the gateway writes the lot before the runner reads any of it and every
   measured read is satisfied from bytes already in the kernel. Nothing suspends, which keeps the
   region on one thread, which is what `GC.GetAllocatedBytesForCurrentThread` counts.
3. **The thread id is asserted afterwards**, so a continuation that did hop fails the test instead
   of quietly measuring an idle thread.
4. **A counter-test that a deliberate allocation is noticed.** A broken instrument reporting zero
   would pass every other assertion in the file. Both existing allocation files carry one and so
   does this.

**And one thing that is new here and must not be left out: the meter has to have a listener
attached.** `Counter<T>.Add` short-circuits when no `MeterListener` is enabled for the instrument,
so a measurement taken with metrics configured but unobserved would report zero for a publish path
that allocates the moment anyone starts collecting. The measured loop runs with a listener
attached and enabled.

- [ ] **Step 1: Write the failing test**

The shape, with the three assertions that make the number mean something:

```csharp
    [Fact]
    public async Task RunAsync_OverASteadyMboStream_AllocatesExactlyNothingPerRecord()
    {
        await using var gateway = new MockLiveGateway(DatasetName);
        var handler = new CountingHandler();

        // Metrics configured *and observed*: Counter<T>.Add short-circuits with no listener, so a
        // measurement taken without one would report zero for a path that allocates the moment
        // anybody starts collecting.
        using var metrics = new LiveSessionMetrics();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LiveSessionMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();

        await using var runner = new LiveSessionRunner(
            Session(gateway), handler, Supervisor(), logger: null, metrics: metrics);

        await StartSessionAsync(gateway, runner);

        await ReplayAsync(gateway, WarmupRecords, firstSequence: 1);
        Assert.Equal(WarmupRecords, await PumpExactlyAsync(runner, WarmupRecords));

        await ReplayAsync(gateway, MeasuredRecords, firstSequence: WarmupRecords + 1);

        Settle();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();

        var decoded = await PumpExactlyAsync(runner, MeasuredRecords);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Equal(MeasuredRecords, decoded);
        Assert.Equal(0L, allocated);
    }
```

`PumpExactlyAsync` is bounded by count rather than by the stream ending, for the reason
`LiveAllocationTests.DrainExactlyAsync` documents: a loop that read until the stream ended would
finish with one read that had to wait on a gateway with nothing left to send — the one read that
suspends, allocates a state machine, and moves the continuation to another thread.

The counter-test:

```csharp
    [Fact]
    public async Task TheMeasurementItself_NoticesADeliberateAllocation()
    {
        // Without this, a broken instrument reporting zero would pass every other assertion in
        // this file. Both existing allocation files carry the same test for the same reason.
        // …identical setup, with a handler whose OnRecord allocates a small array per record…
        Assert.True(
            allocated >= MeasuredRecords * 8L,
            $"A deliberate per-record allocation should have been measured; the instrument "
            + $"reported {allocated} bytes. Either the allocation stopped happening, or this "
            + "measurement is not measuring the loop it claims to.");
    }
```

- [ ] **Step 2: Run it and read the number**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~ExtensionsAllocationTests"`

Expected on the first run: **a real possibility of failure, and that is the point of the task.**
Three candidates, in the order they are worth checking:

| Symptom | Likely cause | Fix |
|---|---|---|
| A constant per-flush cost | the metrics tag being built at the call site | pass the pre-built `_sessionTag` by `in` (Task 10) |
| A per-record cost | `ILiveRecordHandler.OnRecord` dispatching through something that boxes | check the handler is invoked on the interface directly, with no closure |
| ~72 bytes once | an `async Task<int>` helper in the *test* returning a `Task<int>` | inline the loop, as `LiveAllocationTests` does and documents |

The third is a test bug, not a library bug, and charging it to the library would be exactly the
confusion these files exist to prevent.

- [ ] **Step 3: Fix what the measurement found, then rerun until it is zero**

Run: `dotnet test tests/DatabentoDotNet.Extensions.Hosting.Tests --filter "FullyQualifiedName~ExtensionsAllocationTests"`

Expected: PASS, both tests.

- [ ] **Step 4: Run the whole suite and commit**

```bash
dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"
git add tests/DatabentoDotNet.Extensions.Hosting.Tests/ExtensionsAllocationTests.cs \
        src/DatabentoDotNet.Extensions.Hosting/LiveSessionRunner.cs
git commit -m "test(extensions): assert zero bytes per record through the runner

LiveAllocationTests already holds FillBufferAsync/TryNextRecord. This adds what
the runner puts around them: the handler dispatch, the flush, the metrics
publish, and the async state machine holding the loop — which is exactly where a
per-call allocation hides, because a state machine box, a CancellationTokenSource
and a cancellation registration are all invisible in the source.

The meter has a listener attached during the measured loop. Counter<T>.Add
short-circuits when nothing is listening, so a measurement taken with metrics
configured but unobserved would report zero for a path that allocates the moment
anybody starts collecting.

And a counter-test that the instrument notices a deliberate allocation, because a
broken measurement reporting zero would pass every other assertion here.

Refs #<allocation>"
```

---

## Task 12: Extend the AOT probe to drive a host

**Files:**
- Create: `tools/DatabentoDotNet.AotProbe/HostedSessionProbe.cs`
- Modify: `tools/DatabentoDotNet.AotProbe/DatabentoDotNet.AotProbe.csproj` (the fifth
  `ProjectReference`, and `Microsoft.Extensions.Hosting`)
- Modify: `tools/DatabentoDotNet.AotProbe/Program.cs` (one call)

**Interfaces:**
- Consumes: `HostApplicationBuilder`, `AddDatabento*`, `ILiveRecordHandler`, `MockLiveGateway`
  (already linked).
- Produces: nothing. It produces confidence.

**This is a real question rather than a formality.** `$(ShippingProject)` runs the trim and AOT
analyzers over the package, and `TreatWarningsAsErrors` makes each an error — but ILC scans IL, so
a `#pragma warning disable IL2026` silences Roslyn and not ILC. The generated configuration binder,
a DI container built from keyed registrations, and the generic host have never been through ILC in
this repository. #64's argument stands: *an analyzer is not a verification*.

**The probe must *drive* the host, not merely reference the package.** ILC compiles only what it
can reach; a reference nothing calls is trimmed away and proves nothing. `HostedSessionProbe` builds
a `HostApplicationBuilder`, feeds it an in-memory configuration, registers a session against
`MockLiveGateway`, starts the host, sends records, and asserts they arrived.

- [ ] **Step 1: Add the reference**

```xml
    <ProjectReference Include="../../src/DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj" />
```

and, in the package group:

```xml
  <!--
    The generic host itself, not just its abstractions: the probe's question is whether ILC accepts
    a real HostApplicationBuilder, a container built from keyed registrations, and the generated
    configuration binder. Referencing only the abstractions would prove none of that.
  -->
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
```

`Microsoft.Extensions.Hosting` needs a `PackageVersion` entry in `Directory.Packages.props` if the
test project's has not already added one.

- [ ] **Step 2: Write `HostedSessionProbe`**

Same shape as `LiveSessionProbe`: a `ProbeReport.Section`, then `report.Require`/`RequireEqual` per
claim. The claims:

| Claim | Why it is worth a check inside the native binary |
|---|---|
| the host built and started | `HostApplicationBuilder`, the container, and `ValidateOnStart` all survived ILC |
| the configuration bound | the generated binder is reachable and correct after trimming |
| the session reached `Running` | connect, CRAM, subscribe and start work under ILC through the runner |
| every record sent came back, in order | the zero-copy loop works through an interface dispatch under ILC |
| the host stopped cleanly | shutdown, the half-close, and disposal survive |

Configuration comes from `AddInMemoryCollection`, not a file, so the probe stays offline and
self-contained — the property every other probe in this program already has.

- [ ] **Step 3: Call it from `Program.cs`**

```csharp
await HostedSessionProbe.RunAsync(report, cancellationToken).ConfigureAwait(false);
```

after `LiveSessionProbe`.

- [ ] **Step 4: Run the probe under the JIT first**

Run: `dotnet run --project tools/DatabentoDotNet.AotProbe`

Expected: `PASS — N checks in … ms`, with N larger than before. A failure here is a logic bug, and
finding it under the JIT is far cheaper than finding it after an ILC publish.

- [ ] **Step 5: Run the real gate**

Run: `tools/aot-probe.sh`

Expected: the publish succeeds with **zero** ILC warnings — `TrimmerSingleWarn=false` is already
set, so every one is reported rather than collapsed — `file(1)` confirms a native executable, and
the binary prints `PASS`.

If ILC reports warnings the analyzers did not, that is the task doing its job. Fix the code rather
than suppressing the warning; a suppression here would put the package back to being verified by
analyzer alone, which is the state #64 exists to reject.

- [ ] **Step 6: Commit**

```bash
git add tools/DatabentoDotNet.AotProbe Directory.Packages.props
git commit -m "chore(aot): drive a generic host inside the native binary

The trim and AOT analyzers have been on for this package since it was created,
and that is compile-time analysis: ILC scans IL, so a suppression that silences
Roslyn does not silence it. A DI container built from keyed registrations, the
generic host, and the generated configuration binder had never been through ILC.

The probe drives the host rather than referencing the package, because ILC
compiles only what it can reach and a reference nothing calls is trimmed away and
proves nothing.

Refs #<aot>"
```

---

## Task 13: The guide, the package README, and the `HostedLive` sample

**Files:**
- Create: `docs/guides/hosting-and-dependency-injection.md`
- Modify: `docs/guides/toc.yml`
- Modify: `docs/docfx.json` — the fifth project in `metadata.src.files`
- Rewrite: `src/DatabentoDotNet.Extensions.Hosting/README.md`
- Create: `samples/DatabentoDotNet.Samples.HostedLive/` (csproj, `Program.cs`, `appsettings.json`)
- Modify: `DatabentoDotNet.slnx`, `samples/README.md`, root `README.md`
- Modify: `docs/release-notes.md`

**Interfaces:**
- Consumes: everything.
- Produces: the documentation obligation CLAUDE.md imposes — *"If you change behaviour, change its
  guide in the same commit"* — discharged for M6.

**It lands last on purpose.** The behaviour is not settled until Task 12 passes, and a guide written
against unsettled behaviour is a guide that has to be rewritten.

- [ ] **Step 1: Write the guide**

`docs/guides/hosting-and-dependency-injection.md`. **A how-to**, per Diátaxis and CLAUDE.md's four
kinds — the reader is competent and has a task, so no memory model and no tutorial pacing. It opens
with the answer, in one sentence, before anything else.

The sections, and the load-bearing content of each:

| Section | Must say |
|---|---|
| **Lead** | One sentence: `AddDatabento` registers the clients; `AddDatabentoLive` runs a session as a hosted service |
| Install | `dotnet add package DatabentoDotNet.Extensions.Hosting`, and that it brings all four core packages |
| The shortest thing that works | `Program.cs` and `appsettings.json`, side by side, both complete |
| Configuration reference | Every key, its type, its default. The **ISO-8601** duration form spelled out with examples — `PT30S`, `PT5M`, `PT1H30M` — because a reader who writes `30s` gets a startup failure and no other page will tell them why |
| Writing a handler | The `scoped RecordRef` rule stated as a rule: *copy out what you need; the reference is valid for that call only.* And the scope-inside-`OnFlushAsync` recipe |
| Two sessions in one host | The `LiveClient.Dataset` reason — one client is one dataset — and the two-name example |
| Reconnection | That it is on by default; that `MaxAttempts` bounds **consecutive** failures and resets on success, so a flapping gateway reconnects indefinitely; that **every reconnect is a newly billed session**; and that a clean close is not a failure and does not reconnect |
| Health checks and metrics | The state → status table from Task 10, and the four instrument names |
| What is not here, and never will be | `Task<RecordRef>` — an `async` method cannot return a `ref struct`, so a per-record `await` is not available at any price. A caller who wants one uses `LiveClient.RecordsAsync` and pays two allocations per record |
| Version | *"Describes DatabentoDotNet.Extensions.Hosting 1.1.0."* A page with no version stamp is a page nobody can trust |

**Link, never restate.** The guide points at `zero-copy-and-allocation` for *why* the dispatch
contract has the shape it has, at `live-streaming` for the client underneath, and at
`<xref:DatabentoDotNet.Extensions.Hosting.ILiveRecordHandler>` for the member documentation.
`--warningsAsErrors` turns a stale xref into a red build, which is the property that made moving
the guides into this repository worth doing.

- [ ] **Step 2: Add it to the sidebar**

In `docs/guides/toc.yml`, under `Start here`, after `Live Streaming` — a reader who has just read
about live streaming is exactly the reader who wants this next:

```yaml
    - name: Hosting and Dependency Injection
      href: hosting-and-dependency-injection.md
```

- [ ] **Step 3: Add the project to the API reference**

In `docs/docfx.json`, `metadata.src[0].files`:

```json
            "DatabentoDotNet.Extensions.Hosting/DatabentoDotNet.Extensions.Hosting.csproj"
```

This file enumerates projects one by one rather than globbing, and its own comment says why: *"A
glob would silently pick up a fifth project the day one is added, and whether a new project belongs
in the published reference is a decision somebody should make in a diff."* This step is that
decision, and the answer is yes — the package's public surface is what a consumer calls.

- [ ] **Step 4: Write the package README**

Its own, per the #74 convention, and about the one package the reader just installed — not a copy
of the root README, whose relative links resolve on github.com and 404 on nuget.org. Roughly forty
lines: what it is, the `Program.cs` + `appsettings.json` pair, and absolute links to the guide and
the API reference.

- [ ] **Step 5: Write the sample**

`samples/DatabentoDotNet.Samples.HostedLive`, matching all four existing samples exactly:

- A header comment saying what it does, how to run it, and — **in capitals — that it costs money**,
  printed before `StartAsync` rather than after, because everything up to `start_session` is free.
- Its key from `DATABENTO_API_KEY` and nothing else. **No `.env`.** That is harness machinery, and a
  sample that copied it would teach a reader to keep credentials in their source tree.
- A record ceiling so it stops on its own. A sample that runs until interrupted is a sample somebody
  leaves running.
- `samples/Directory.Build.props` supplies `IsPackable`, `IsTestProject` and `IsSampleProject`, so
  the project file carries only its `ProjectReference` and its `appsettings.json` copy rule.

It is the **first sample with an `appsettings.json`**, which is the point of it: what it demonstrates
is configuration, and a hosted sample that configured itself in code would demonstrate nothing the
`LiveStream` sample does not.

- [ ] **Step 6: Update the three indexes**

- `DatabentoDotNet.slnx` — the sample under `/samples/`.
- `samples/README.md` — "Four console programs" becomes five, a table row, and a line in
  *What they cost* noting that this one streams until its ceiling like `LiveStream` does.
- Root `README.md` — a fifth row in the package table with its NuGet badge, and a fifth
  `dotnet add package` line.

- [ ] **Step 7: Write the release note**

In `docs/release-notes.md`, a `1.1.0` section: the package, the `HttpMessageHandler` seam that
shipped in 1.0 to enable it, and the fact that the core four are unchanged. Note that
`PublicAPI.Shipped.txt` for the new package stays empty through 1.1.0, for the same reason the core
four's did through 0.9.x — `Shipped` lists a surface undertaken not to break, and that undertaking
should follow evidence rather than precede it.

- [ ] **Step 8: Build the site with warnings as errors**

```bash
dotnet tool restore && dotnet docfx docs/docfx.json --warningsAsErrors
```

Expected: success. A DocFX warning here is nearly always an unresolved cross-reference, which ships
as a dead link nobody reports.

- [ ] **Step 9: Build everything, including the sample**

```bash
dotnet build && dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"
```

Expected: PASS. CI builds the samples because they are in the solution and cannot run them.

- [ ] **Step 10: Commit**

```bash
git add docs samples DatabentoDotNet.slnx README.md src/DatabentoDotNet.Extensions.Hosting/README.md
git commit -m "docs(extensions): the hosting guide, the package README, and a sample

A how-to rather than a tutorial: the reader is competent and has a task. It opens
with the answer and links rather than restating — zero-copy-and-allocation for why
the dispatch contract has the shape it has, live-streaming for the client
underneath, and an xref for the member documentation, which
--warningsAsErrors turns into a red build if it ever goes stale.

The configuration reference spells out the ISO-8601 duration form, because a
reader who writes \"30s\" gets a startup failure and no other page would tell them
why.

docfx.json enumerates projects rather than globbing precisely so that adding one
to the published reference is a decision somebody makes in a diff. This is that
diff.

The sample is the first with an appsettings.json, which is the point of it: what
it demonstrates is configuration.

Fixes #<docs>"
```

---

## Self-review

Run against the spec after the plan was written, and after the four corrections in C1–C4 were
folded back into it.

### 1. Spec coverage

| Spec section | Covered by |
|---|---|
| §1 the clients are not equally container-shaped | T5 — `TryAddSingleton`, the shared transport, and the four registration tests that pin both orders |
| §1 `AddDatabentoReference` composes either way | T5, `AddDatabentoReferenceThenHistorical_StillYieldsOneTransport` |
| §2a `HttpMessageHandler` seam | T2, in full, including the five tests |
| §2b `TryFromWireString` | **Deleted — C1.** It already exists. T1 removes it from the spec |
| §3 package boundary, one project, four references | T3 |
| §3 naming, `Microsoft.Extensions.DependencyInjection` for `Add*` | T3, T5 |
| §3 the four new transitive dependencies | T3, with the health check package corrected to the implementation half and the reason recorded |
| §4 registration is always named; `"Default"` literal | T5, `AddDatabentoLive_WithNoName_UsesTheLiteralDefaultName` |
| §4 options are bindable primitives | T4 |
| §4 one conversion path, shared by validation and construction | T4 (`LiveSessionResolver`), T5 (`LiveSessionValidator`), T9 (`CreateRunner`) |
| §4 failures name their configuration path | T4, six tests |
| §4 API key precedence, and ROADMAP question 5 | T4 (three tests), T1 (the ROADMAP edit) |
| §4 sessions are declared in code, never conjured from keys | T5, stated in the file and asserted by there being no scan |
| §5 runner holds the loop, service holds almost nothing | T7, T9 |
| §5 startup splits by exception type | T8 (`IsTransient`), T7 (`StartSessionAsync_WithAKeyTheGatewayRejects_Faults`) |
| §5 synchronous `ref struct` dispatch, zero allocation | T7, T11 |
| §5 handler is a singleton | T5 (`AddRecordHandler`), documented on the interface in T7 |
| §5 a handler exception is fatal | T7, two tests |
| §5 reconnect order, backoff, jitter not configurable, `MaxAttempts` consecutive | T6, T8 |
| §5 shutdown half-closes within a bounded slice | T7 (`CloseAsync`), T9 (`StopAsync_ClosesTheSession`) |
| §6 metrics count locally, publish at flush | T10, and T11 asserts it costs nothing |
| §6 `[LoggerMessage]` partials, stable ids, never per record | T7 (`ExtensionsLog`) |
| §6 health checks opt-in | T10 |
| §7 the whole test table | T4, T5, T7, T8, T10, T11 |
| §7 no new billable test | Held: nothing in T1–T13 starts a real session, and CLAUDE.md's free/billable table gains no row |
| §8 binding generator verified in a library | **Resolved before the plan — C2.** T3 sets the property and guards it |
| §8 the AOT probe drives a host | T12 |
| §8 `PooledConnectionLifetime` via `Duration.ToTimeSpan()` | T5 |
| §9 the six rejected alternatives | Each restated where it would otherwise be reversed: T2 (full `HttpClient`), T7 (async-per-record, no `Channel<T>`), T3 (no package split), T1+T4 (no second wire-string parser), T5 (no auto-registration from configuration), T1 (not everything in 1.0) |
| §10 issues, milestones, labels, order | T1, Step 8 |

**No gaps.** One deletion (§2b) and one resolution (§8's spike), both recorded in C1–C4 rather than
silently dropped.

### 2. Placeholders

Searched for `TBD`, `TODO`, `implement later`, `add appropriate error handling`, `similar to Task`,
and `write tests for the above`. None present.

Two deliberate ellipses remain and are marked as such, both in test files whose surrounding lines
are real, per CLAUDE.md's `// …` rule: `OptionsValidationTests.Flatten` and the two one-line handler
doubles in `RegistrationTests`. Both are named, their signature is given, and what they must do is
stated in the sentence beneath them.

Tasks 9 through 13 give their tests as a table of name → assertion rather than as full source. That
is a deliberate scaling decision, not a placeholder: by that point the harness — `MockLiveGateway`,
`ServeStartupAsync`, `RecordingHandler`, `Session(gateway)` — is fully written out in Tasks 7 and 8,
and repeating two hundred lines of it four more times would bury the five lines that differ.

### 3. Type consistency

Checked across tasks:

- `LiveSessionResolver.Resolve(string, LiveSessionOptions, DatabentoOptions, string?)` — same four
  parameters in T4 (definition), T5 (`LiveSessionValidator`), T9 (`CreateRunner`).
- `LiveSessionResolutionResult.Succeeded` / `.Session` / `.Failures` — same three members throughout.
- `ResolvedReconnect.Default` — introduced in T4, used in T7, T8, T10.
- `ReconnectSupervisor(ResolvedReconnect)` with `Jitter` and `Delay` as `init` properties — same in
  T6 (definition), T7 (`CloseAsync` uses `Delay`), T8 (three tests replace both).
- `LiveSessionRunner(ResolvedLiveSession, ILiveRecordHandler, ReconnectSupervisor, ILogger<LiveSessionRunner>?)`
  — T7 defines it; T10 appends one optional `LiveSessionMetrics?` parameter, and T11's call site uses
  the five-parameter form. **T7's `Produces` block shows the four-parameter form and T10's shows the
  fifth**; an implementer reading only T11 sees `logger: null, metrics: metrics` as named arguments,
  which is unambiguous either way.
- `ILiveRecordHandler.OnRecord(scoped RecordRef)` / `OnFlushAsync(CancellationToken)` — T5's
  placeholder and T7's documented version are byte-identical in signature.
- `LiveSessionState` — the six members are named identically in T7 (definition), T8
  (`Reconnecting`), T10 (the mapping table).
- `DatabentoLiveBuilder.AddRecordHandler` / `.AddHealthCheck` — T5 defines the first two overloads
  and says `AddHealthCheck` arrives in T10; T10 adds it.
- `ExtensionsLog` event ids 1–6 — assigned once, in T7, and used unchanged in T8 and T10.
- `LiveSessionMetrics.MeterName` — T10 defines it; T11 reads it in the listener filter.
- `ReadTimeout` — added to `LiveSessionOptions`, `ResolvedLiveSession` and the resolver in T4, used
  in T8.

One naming point worth stating because it is the kind of thing that drifts: the runner's counter is
`RecordsReceived` (a `long` property) and the metrics method is also `RecordsReceived` (taking a
`long` and a tag). They are on different types and that is deliberate — the property is what the
health check and the tests read, the method is what publishes it — but do not rename one to
"disambiguate" them without renaming the instrument too.

### 4. Scope

Thirteen tasks, one milestone, one package plus one additive change to a shipped one. Not a
candidate for decomposition: T2 is the only piece that ships separately (in 1.0), and it is one
property pair with five tests.

---

## What this plan does not do

Stated so that its absence is a decision rather than an oversight.

- **No encoder, no `Channel<T>` pump, no async-per-record contract, no package split.** Spec §9.
- **No automatic registration of sessions found in configuration.** Spec §9, and T5 states it in the
  file where somebody would otherwise add it.
- **No new billable test.** Everything is settled by `MockLiveGateway`. The fact only a real session
  can establish is already owned by `RealGatewaySessionTests`; a second one here would spend money
  to learn nothing new, which is the inverse of CLAUDE.md's rule.
- **No `InternalsVisibleTo`.** C4 resolves the pressure toward one by making the runner public,
  which is what spec §5's own argument implies.
- **No change to the core four's dependency closure.** The four new packages reach consumers of
  `DatabentoDotNet.Extensions.Hosting` only; #71 and #74's eight-package closure is untouched.
- **`PublicAPI.Shipped.txt` is not populated.** It stays empty through 1.1.0, for the reason
  `Directory.Build.targets:73-80` gives.
