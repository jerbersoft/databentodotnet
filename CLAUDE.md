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
```

Requires the .NET 10 SDK or newer.

---

## Layout

```
src/DatabentoDotNet.Dbn/            DBN codec — records, metadata, decoder, symbol maps
src/DatabentoDotNet.Live/           live gateway client — in progress through M2
tests/DatabentoDotNet.Dbn.Tests/
tests/DatabentoDotNet.Live.Tests/   the client's tests, and the mock gateway they run against
ROADMAP.md                          milestones, architecture, decisions
PORTING.md                          Rust → .NET mapping guide
```

The `.Historical` and `.Reference` source projects arrive at M3–M4.

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
| `dbn` | 0.68.0 | not cloned locally — the **codec lives here**, not in `databento-rs` |
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

Decoder conformance target: decode every `.dbn` and `.dbn.zst` fixture in
`databento-rs/tests/data/` and round-trip re-encode byte-identically.
