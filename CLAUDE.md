# CLAUDE.md

Operating guide for this repo. Read `ROADMAP.md` for *what* we're building and `PORTING.md`
for *how* the Rust source maps to .NET.

---

## What this is

A .NET client for [Databento](https://databento.com) market data. Databento ships official
clients for Python, C++, and Rust — but not .NET, so this fills that gap.

**Priority order: (1) real-time live streaming, (2) historical market data.**

This is largely a **port**, not a from-scratch build.

---

## Workflow: an issue exists before work starts

**Every change begins with a GitHub issue.** Issues live in this repo
(`jerbersoft/databentodotnet`) — GitHub is the tracker for this project only, not a
convention that carries to other repos.

Before writing code:

1. **Find or create the issue.** No issue → no work. This includes roadmap items, bugs, and
   chores. `gh issue list --milestone "M1: DBN codec"`
2. **Assign a milestone** (M0–M5). Milestones track the roadmap; see below.
3. **Label it** with one `type:` and at least one `area:`.
4. **Reference it in commits and PRs** — `Fixes #12` / `Refs #12`.

### Issues use BLUF

**Bottom Line Up Front.** The first one or two sentences say what ships and why. A reader
should not have to scroll to learn what an issue is about. Detail goes below the BLUF, never
above it.

The issue forms in `.github/ISSUE_TEMPLATE/` make BLUF a required field, so this is enforced
rather than remembered. Task issues carry: **BLUF → Scope → Definition of done → References →
Porting notes.**

Write a definition of done specific enough to disagree with. "Decoder works" is not one;
"all 17 sizes assert green against the databento-cpp values" is.

### Labels

Two axes plus meta. Apply **one `type:`** and **at least one `area:`**.

| `type:` — what kind of work | |
|---|---|
| `type: feature` | New capability |
| `type: bug` | Defect in existing behavior |
| `type: docs` | Documentation only |
| `type: chore` | Build, CI, tooling, packaging |
| `type: test` | Test coverage or test infrastructure |
| `type: perf` | Performance or allocation work |

| `area:` — where in the codebase | |
|---|---|
| `area: dbn` | DBN codec — records, metadata, decoder |
| `area: live` | Real-time TCP gateway client |
| `area: historical` | Historical HTTPS/REST client |
| `area: reference` | Security master, corporate actions |
| `area: build` | Solution, CI, packaging, release |

| meta — apply as needed | |
|---|---|
| `blocked` | Blocked on another issue or an external dependency |
| `breaking-change` | Breaks public API — tracked closely pre-1.0 |
| `upstream` | Depends on Databento upstream behavior, docs, or a spec change |

**There are deliberately no priority labels.** The roadmap is strictly sequenced and milestones
already encode order; a priority axis would be a second, conflicting ordering.

### Milestones, not labels, for M0–M5

M0 Foundation · M1 DBN codec · M2 Live streaming · M3 Historical · M4 Reference data ·
M5 Polish and 1.0. Native milestones give progress bars and filtering for free.

---

## Commands

```sh
dotnet build          # both TFMs if a .NET 11 SDK is present, else net10.0
dotnet test
dotnet pack -c Release

# Throughput and allocated-bytes-per-record. Release only; BenchmarkDotNet refuses a Debug build.
dotnet run -c Release --framework net10.0 --project benchmarks/DatabentoDotNet.Benchmarks -- --filter '*'

# Native AOT, end to end: publishes a binary with PublishAot and runs it. Needs a native toolchain
# (clang and the platform linker). The RID defaults to the host's. Not part of `dotnet build` or
# `dotnet test` — the `Native AOT` workflow is what runs it on every push.
tools/aot-probe.sh

# The four samples. Each takes its key from DATABENTO_API_KEY and nothing else — no .env, which is
# harness machinery a sample must not teach — and each moves billable data and says so before it
# does. CI builds them (they are in the solution) and cannot run them. See samples/README.md.
dotnet run --project samples/DatabentoDotNet.Samples.HistoricalRange

# Code generation. Neither runs during a build: both emit committed source, so their output is a
# diff somebody reads rather than a build artefact nobody sees. Run them when their input changes.
python3 tools/generate-publishers.py ../dbn/src/publishers.rs
python3 tools/generate-reference-codes.py tests/DatabentoDotNet.Reference.Tests/Data

```

Requires the .NET 10 SDK or newer.

---

## Layout

```
src/DatabentoDotNet.Dbn/            DBN codec — records, metadata, decoder, symbol maps
src/DatabentoDotNet.Live/           live gateway client
src/DatabentoDotNet.Historical/     historical HTTPS client — transport, timeseries, batch, symbology
src/DatabentoDotNet.Reference/      security master, corporate actions, adjustment factors
tests/DatabentoDotNet.Dbn.Tests/
tests/DatabentoDotNet.Live.Tests/   the client's tests, and the mock gateway they run against
tests/DatabentoDotNet.Historical.Tests/   and the mock historical gateway
tests/DatabentoDotNet.Reference.Tests/
benchmarks/DatabentoDotNet.Benchmarks/   throughput and allocation figures — ships nothing
tools/DatabentoDotNet.AotProbe/     the Native AOT end-to-end check — ships nothing
tools/aot-probe.sh                  publishes that probe natively and runs it
samples/                            four runnable console programs — ships nothing
docs/plans/                         working material; there is no published site (#70)
ROADMAP.md                          milestones, architecture, decisions
PORTING.md                          Rust → .NET mapping guide
```

### Where a documentation page goes

**Three homes, one canonical copy of each fact.** This is not a preference; #67 wrote seven prose
pages that duplicated the wiki, and #69 deleted them. Before writing documentation anywhere, decide
which of these it is:

| Content | Home | Why |
|---|---|---|
| Guides, explanations, troubleshooting, FAQ, release narrative | **The wiki** | Task-oriented, stable across versions, and grows from questions rather than from commits |
| API reference | **XML doc comments** | `dotnet pack` ships the `.xml` inside the package, so it reaches IntelliSense at the call site. Nothing to publish, nothing that can drift |
| Repository conventions, workflow, testing gates | **This file** | Binds every contributor at the commit they are working on |
| Design decisions and their reasoning | `ROADMAP.md` / `PORTING.md` | A decision changing *is* a code change |
| What the project is, install, a short example | `README.md` | Must be true of the commit it ships with |

**There is no documentation site, and #70 is why.** One was built (#67), cut to the API reference
(#69), and retired (#70) inside a single evening — a rendered copy of the doc comments is not
*wrong*, since it is generated, but it is a second surface to host, publish, link and keep
reachable for a fact the reader already has in their editor. Do not add one back without an issue
that says what changed about that argument.

The wiki is a **separate git repository** and so is invisible from this working tree, which is
exactly how #67's duplication happened. It is at `https://github.com/jerbersoft/databentodotnet.wiki.git`,
cloned as a sibling at `../databentodotnet.wiki`, and its own `Wiki-Style-Guide` page states the
rule this table restates: *does this fact change when the code changes?* If yes it belongs in the
repository; if no it belongs in the wiki. **Read the wiki before adding prose to `docs/`.**

The benchmark project is excluded from `dotnet test` and from `dotnet pack`, by two properties in
its own file — `IsTestProject=false` and `IsPackable=false`. Neither is decorative: without the
first, `dotnet test` finds the assembly (xunit's adapter reaches it transitively through the Live
test project it references for `MockLiveGateway`) and reports a catastrophic failure for a project
with no tests.

`DatabentoDotNet.AotProbe` carries the same two properties, plus `IsProbeProject=true`. That third
one excludes it from `$(ShippingProject)` and so from #63's public API lock — it publishes no
package, and it compiles `MockLiveGateway` by `<Compile Link>`, which RS0016 would otherwise demand a
baseline for. It is *not* excluded from the AOT and trim analyzers the way the benchmark project is:
being analysed is the entire point of it.

The four samples carry the same three, as `IsSampleProject=true`, and carry them in
`samples/Directory.Build.props` rather than in the project files. That placement is the decision, not
an economy: none of the three properties is about the sample, and a reader who copies one of these
out of the tree should get the `ProjectReference` and nothing else. **That file must import the root
`Directory.Build.props` explicitly** — MSBuild stops at the first one it finds walking up, so without
the import the samples would silently lose `net10.0`, `TreatWarningsAsErrors` and the NodaTime ban,
and would still build.

---

## Conventions

### Naming: `DatabentoDotNet.*` everywhere

Package IDs, assemblies, namespaces, projects, and the solution. **Never `Databento.*`** —
that is the vendor's namespace, and an unreserved NuGet prefix they could claim at any time.
"DotNet" also reads as .NET rather than .NET Framework.

### Target frameworks

`net10.0` only.

> ⚠️ **There is no conditional compilation in this repo. Do not add any.** A `net11.0` target
> existed until #16 and carried the codebase's only `#if`. It was removed because the .NET 11
> preview SDK is deliberately not installed on dev machines, so that branch was compiled
> nowhere — written, reviewed and shipped without ever passing a compiler — and CI inferred the
> target from the installed SDK, meaning a failed preview-SDK resolution silently dropped it and
> still went green.

Zstandard, which DBN uses for transport compression, comes from `ZstdSharp.Port` — pure managed,
no P/Invoke, no native asset, no per-RID build.

**Every zstd call goes through `Internal/ZstdDecompressor.cs`.** Keep it that way. .NET 11 adds
`System.IO.Compression.ZstandardStream` to the BCL, so restoring the target at GA — when the
branch can actually be compiled and tested locally before anyone relies on it — is a one-file
change.

`DatabentoDotNet.Live` needs it too, for a session that negotiated `compression=zstd`, and gets it
by **linking that same file** (`<Compile Include="../DatabentoDotNet.Dbn/Internal/…" />`) rather
than through an `InternalsVisibleTo` or a public re-export. One file is still one file, which is
the whole point of the rule; the repo declares no `InternalsVisibleTo` anywhere and this is not
worth being the first.

### Restore

`nuget.config` pins restore to nuget.org with `<clear />`. This machine has a private Telerik
feed configured globally; without the pin, central package management fails and a public
library could resolve packages from a private feed.

### Dates and times: NodaTime, never the BCL

**All date and time processing uses [NodaTime](https://nodatime.org).** The BCL's `DateTime`,
`DateTimeOffset`, `DateOnly`, `TimeOnly`, and `TimeSpan` do not appear in this codebase — not
in the public API, not in internal helpers, not in tests.

**This is enforced, not remembered.** `BannedSymbols.txt` at the repo root lists all five;
`Microsoft.CodeAnalysis.BannedApiAnalyzers` reports each use as RS0030, and
`TreatWarningsAsErrors` makes that a build failure whose message names the NodaTime
replacement. It applies to the test project too — a test that reaches for `DateTime` to build
an expected value is exactly where a 100 ns truncation gets laundered into a passing
assertion.

| Concept | Use | Never |
|---|---|---|
| A point on the timeline | `Instant` | `DateTime`, `DateTimeOffset` |
| A calendar date, no zone | `LocalDate` | `DateOnly` |
| A wall-clock date and time | `LocalDateTime` | `DateTime` |
| A time of day | `LocalTime` | `TimeOnly` |
| An elapsed amount | `Duration` | `TimeSpan` |
| A time in a specific zone | `ZonedDateTime` | `DateTimeOffset` |

This is not only a style preference. `Instant` carries **true nanosecond precision**, and a
`DateTime` tick is 100 ns — so the BCL literally cannot represent a DBN timestamp. Feeding
`1609160400000000001` through `DateTime` returns `…000`; through `Instant` it round-trips
exactly.

#### The wire boundary: record fields stay `ulong`

**Record struct fields remain `ulong` nanoseconds, and this rule does not bend.** Records are
reinterpreted in place over the read buffer, so a field's type *is* its wire layout.
`Instant` is 16 bytes and `LocalDate` is 4; the wire has an 8-byte `u64`. A NodaTime type in
a record struct is silent data corruption, not a compile error.

So the split is: **`ulong` in the structs and the codec, NodaTime at every boundary above
them** — conversions, symbol maps, metadata, and anything a consumer calls.

#### `UndefTimestamp` does not survive a naive conversion

DBN's undefined-timestamp sentinel is `ulong.MaxValue`. `Duration.FromNanoseconds` takes a
`long`, and the obvious cast wraps silently:

```csharp
Duration.FromNanoseconds((long)DbnConstants.UndefTimestamp)   // -1 ns. No exception.
```

That resolves to an `Instant` one nanosecond *before* the Unix epoch — a confidently wrong
answer of exactly the kind this codebase exists to prevent. The sentinel is no safer as a
date: it floor-divides to an entirely ordinary-looking day in 2554.

**`DbnTime` is the one crossing, and every one of its conversions checks the sentinel first.**
`TryToInstant` / `TryToUtcDate` report "no timestamp" by returning `false`; `ToInstant` /
`ToUtcDate` throw. Do not add a second conversion path that skips the check.

The same ceiling applies without the sentinel: `long.MaxValue` nanoseconds is the year 2262.
`DbnTime` therefore splits into whole days plus a nanosecond-of-day remainder rather than
going through a single `long` count, so every `ulong` below the sentinel converts exactly —
`ulong.MaxValue - 1` is 2554-07-21T23:34:33.709551614Z, not an overflow.

---

## Porting rules

The Rust source is authoritative for **functionality, wire format, and behavior**. It is
**not** authoritative for structure. Where a Rust construct exists only to satisfy the borrow
checker or work around a missing language feature, use the .NET equivalent.

Reference clones:

| Source | Version | Location |
|---|---|---|
| `databento-rs` | 0.60.0 | `../databento-rs` (sibling) |
| `dbn` | 0.68.0 | `../dbn` (sibling) — the **codec lives here**, not in `databento-rs` |
| `databento-cpp` | — | struct-size `static_assert`s, the layout oracle |

`PORTING.md` has the full mapping. The load-bearing points:

- **Zero-copy is the reason this library exists.** Records are reinterpreted in place over the
  read buffer. Do not add a copy for API convenience on the low-level path.
- **Do not use `System.IO.Pipelines` for the live socket.** It is the reflexive .NET choice and
  it is wrong here: `ReadOnlySequence<byte>` may be non-contiguous, which breaks
  `MemoryMarshal.AsRef<T>`, and it adds a second buffering layer over the FSM's own.
- **Async reads go through `DbnFsm.SpaceMemory()`, never `Space()`.** There is no
  `ReadAsync(Span<byte>)` and no `Memory<T>` reinterpret cast, so a `MemoryManager<byte>` owned by
  `AlignedBuffer` projects its `ulong[]` as a `Memory<byte>`. `Space()` is derived from it so the
  two views cannot drift. Decided in #15 — PORTING.md §1 has the rejected alternatives.
- **There is no `Task<RecordRef>`, and there never can be.** An `async` method cannot return a
  `ref struct`, so upstream's `LiveClient::next_record()` does not port; its `fill_buf()` /
  `try_next_record()` pair does. A `RecordRef` *local* inside an `async` method is fine — only
  one that survives an `await` is rejected (CS4007), which is the lifetime rule the FSM already
  imposes.
- **Drop `State::Consume` from the FSM.** It models nothing about DBN; it exists purely to
  defer a mutation Rust's borrow checker forbids.
- **Type-state builders → `required` init properties.** Rust uses generic type-state for what
  C# 11 has natively.
- **`Result<T,E>` → exceptions for exceptional cases, `Try*` for expected ones.** A stream
  ending is not an exception.
- **Timestamps stay `ulong` nanoseconds on the wire; NodaTime above it.** A `DateTime` tick is
  100 ns and would silently truncate; `Instant` is exact. See "Dates and times" above — the
  conversion is explicit, never implicit, and it checks the `ulong.MaxValue` sentinel.

---

## Testing

The highest-value test in the repo asserts `Unsafe.SizeOf<T>()` for every record against the
`static_assert` values in `databento-cpp`. Records are reinterpreted directly over the read
buffer, so a layout mistake is **silent data corruption**, not an exception — these assertions
turn it back into a build failure. Add one for every record struct ported.

Upstream ships a mock live gateway in `databento-rs/src/live/client.rs`'s test module. It is
ported, not reinvented, and it landed before the client: `MockLiveGateway` in
`tests/DatabentoDotNet.Live.Tests` (#18). Test M2 work against it rather than against a new
double, and see PORTING.md §2 for where it deliberately departs from upstream's.

**The mock cannot confirm what it shares an author with.** It and the client were written from the
same reading of `live/protocol.rs`, so a misreading of the metadata block or the record framing
would sit in both and they would agree with each other — `StubLiveClient` included, which is a
second opinion from the same source rather than a second source. Only a real gateway settles that,
and only after `start_session`. `RealGatewaySessionTests` is the one test that crosses that line,
and **it is the only test in the repo that moves billable data**, so it carries `DATABENTO_LIVE_SESSION`
as a second gate on top of `Category=Live`. The rule is *no test starts a session without its own
opt-in* — not that no test may ever start one. Everything in `RealGatewaySmokeTests` stops short of
that line and is therefore free; keep it that way.

**The same argument runs for M3, and the line falls in a different place.** `MockHistoricalGateway`
and the historical client were written from the same reading of Databento's HTTP documentation, so
`RealHistoricalApiTests` (#44) calls the endpoints for real, behind `Category=Historical` — filtered
out of CI by name alongside `Category=Live`. The historical API separates cost *by endpoint* rather
than by one line in a session, so every `metadata.*` endpoint, `symbology.resolve` and the `batch`
read endpoints are discovery or billing enquiries and cost nothing; `timeseries.get_range` and
`batch.submit_job` do, and carry `DATABENTO_HISTORICAL_REQUEST` as their second gate. That gate
shipped with the harness, ahead of anything that uses it.

That first real call found #45 — `get_dataset_condition` reads `end_date` as inclusive while
`DateRange` models it as exclusive — which is the whole argument for these tests restated as
evidence: the mock had agreed with the client about it for as long as both existed. **Fixing it
taught the same lesson a second time.** `metadata.list_datasets` takes the identical `DateRange`,
and upstream documents nothing about *its* end (`metadata.rs:41-50`), so the obvious fix — convert
in the one shared renderer — was checked against the real API before it was written rather than
after. `list_datasets` turned out to be genuinely half-open, so the shared fix would have broken it
silently. Probe the endpoint you are about to change, not the one next to it.

**The free/billable split is by file, so it stays checkable by reading a file list** rather than by
auditing every method in a large class for a call that slipped in:

| Free — key and category only | Billable — plus a second gate |
|---|---|
| `RealGatewaySmokeTests` | `RealGatewaySessionTests` (`DATABENTO_LIVE_SESSION`) |
| `RealHistoricalApiTests`, `RealBatchApiTests` | `RealTimeseriesDownloadTests`, `RealBatchSubmitTests` (`DATABENTO_HISTORICAL_REQUEST`) |
| `RealReferenceApiTests` | `RealReferenceRequestTests` (`DATABENTO_REFERENCE_REQUEST`) |

A billable call added to a file in the left column is a review finding, not something discovered
from a bill. Keep the columns honest when adding a test.

**Zero-per-record allocation is asserted, not asserted-to.** `AllocationTests` and
`LiveAllocationTests` measure `GC.GetAllocatedBytesForCurrentThread()` around a steady-state loop
and require exactly zero — over the whole vendored corpus, and over the mock gateway's socket.
Both files also contain a test that the *measurement itself* notices a deliberate allocation,
because a broken instrument reporting zero would pass every other assertion in them. Anything added
to the `FillBufferAsync`/`TryNextRecord` path has to keep those green; the benchmark project
reports the same numbers but enforces nothing, since a benchmark someone has to remember to run
cannot hold a guarantee.

**Native AOT is verified by publishing and running a binary, not by the analyzers alone.**
`tools/DatabentoDotNet.AotProbe` references all four packages and reaches into each — ILC compiles
only what it can reach, so a reference nothing calls is trimmed away and proves nothing. It decodes
the whole vendored corpus to the counts `DbnDecoderTests` asserts, from the *same*
`ExpectedRecordCounts` table, which both projects compile rather than copy. `tools/aot-probe.sh`
publishes it, checks with `file(1)` that what came out is native, and runs it; nothing in the process
can make that check itself, because `PublishAot` writes the `IsDynamicCodeSupported=false` switch
into the ordinary build's `runtimeconfig.json` too. The publish is an independent gate rather than a
slower rerun of the analyzers: ILC scans IL, so a `#pragma warning disable IL2026` that silences
Roslyn does not silence it. See ROADMAP §7.

Decoder conformance target: decode every `.dbn`, `.dbn.zst`, and `.dbn.frag` fixture in the
vendored corpus at `tests/DatabentoDotNet.Dbn.Tests/Data/` (71 files from `databento/dbn` 0.68.0
— see that directory's `README.md`), and yield the record counts upstream reports for each.

> This previously read "…and round-trip re-encode byte-identically." **There is no record
> encoder, only `MetadataEncoder`, and there is deliberately not going to be one.** This library
> reads market data; nothing in it writes DBN, so an encoder would be a large public surface
> maintained for no consumer — and a stated target that no issue is working toward is worse than
> no target. If writing `.dbn` files ever becomes a real requirement, it gets an issue and this
> line changes back.
