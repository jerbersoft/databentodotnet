# databento-dotnet — Roadmap

> **Work is tracked as GitHub issues in this repo.** Every milestone below links to its
> issue(s). No work starts without one — see [CLAUDE.md](CLAUDE.md#workflow-an-issue-exists-before-work-starts).

A .NET client for [Databento](https://databento.com) market data.
**Priority order: (1) real-time live streaming, (2) historical market data.**

Research basis: Databento's official clients — [`dbn`](https://github.com/databento/dbn) (Rust,
the DBN reference implementation), [`databento-rs`](https://github.com/databento/databento-rs),
[`databento-cpp`](https://github.com/databento/databento-cpp) — read at 2026-08-26.

This is largely a **port**, not a from-scratch build. See **[PORTING.md](PORTING.md)** for the
Rust→.NET mapping: which upstream constructs to carry over structurally, which to replace with
.NET patterns, and the behavioral details that are easy to drop in translation.

---

## 0. Context & key findings

### There is no official .NET client
Databento maintains clients for **Python, C++, and Rust** only. The Rust `dbn` crate is the
normative definition of the wire format; `databento-cpp` mirrors it with `static_assert`s on
every struct size. Those asserts are our conformance target.

Community prior art on NuGet (neither is official):
- `Databento.Client` (Alparse) — live + historical, ~21k downloads.
- `Databento.CSharpApiClient` — historical only.

### .NET 11 is the right target, but it is still preview
- Latest SDK: **`11.0.100-preview.7`** (2026-08-11). **GA is 2026-11-10**, and .NET 11 is
  **STS** (2-year support), not LTS. .NET 12 (Nov 2027) is the next LTS.
- This machine has **10.0.102** — the .NET 11 preview SDK is not installed yet.
- **The decisive .NET 11 feature for us: `System.IO.Compression.ZstandardStream`.** DBN's
  transport compression is Zstandard. On .NET 10 that means a native dependency
  (`ZstdSharp.Port` etc.); on .NET 11 it is in the BCL with no native asset and no P/Invoke.
  That alone justifies the target.

**DECIDED, then REVERSED: `net10.0` only.**

The original decision was to multi-target `net10.0;net11.0`, using the BCL's `ZstandardStream`
under `net11.0` and `ZstdSharp.Port` (pure-managed, no P/Invoke) under `net10.0`, so the codec
would ship dependency-free on the newer framework.

**Reversed in [#16](https://github.com/jerbersoft/databentodotnet/issues/16)** once M1 actually
used zstd. The .NET 11 preview SDK is deliberately not installed on dev machines, so the
`#if NET11_0_OR_GREATER` branch was compiled *nowhere* — written, reviewed and shipped without
ever passing a compiler. Worse, `Directory.Build.props` inferred the target from the installed
SDK, so a failed preview-SDK resolution in CI would silently drop it and the matrix would still
go green. A guard step was added for precisely that; needing one was the signal.

An unverifiable code path is worse than one well-tested dependency. Restore at .NET 11 GA, when
the branch can be compiled and tested locally before anyone relies on it — every zstd call still
routes through `Internal/ZstdDecompressor.cs`, so that is a one-file change.

`DatabentoDotNet.Dbn` carries exactly one third-party dependency: `ZstdSharp.Port`.

### Naming

**DECIDED: `DatabentoDotNet.*` everywhere** — NuGet package ID, assembly name, root namespace,
project directory, and solution. No split between packaging and code identity.

Two reasons:

1. **`Databento.*` is the vendor's namespace, not ours.** This is a third-party client;
   shipping assemblies under the company's name misrepresents provenance.
2. **The `Databento.*` NuGet prefix is unreserved** (`Databento.Client` is unverified). If
   Databento reserves it, anything we published under it would be blocked.

"DotNet" also disambiguates from .NET Framework, which a bare `.Net` suffix does not.

IDs confirmed available 2026-08-26: `DatabentoDotNet`, `DatabentoDotNet.Dbn`,
`DatabentoDotNet.Live`, `DatabentoDotNet.Historical`, `DatabentoDotNet.Reference`.
**Publish placeholders early to claim them.**

### Core .NET design decisions

**Records are `readonly struct` with `[StructLayout(LayoutKind.Sequential)]`.**
The Rust structs are `#[repr(C)]`, alignment 8, no padding. Sequential layout with natural
alignment reproduces this exactly. Every struct gets a size test (see §2).

**Zero-copy decode.** Read into a pooled buffer, then reinterpret in place:
`ref readonly T rec = ref MemoryMarshal.AsRef<T>(span)` — no copy, no allocation per record.
This is the whole performance argument for a .NET client and should not be compromised for
API convenience.

**The `ref struct` / `async` tension — resolved in #15; ship two APIs.** Rust returns
`RecordRef<'_>` borrowed from the read buffer. A C# `ref struct` cannot be *preserved across* an
`await` (CS4007) — it may be a local in an `async` method, it just may not survive the await, which
is the same one-record-at-a-time lifetime `DbnFsm` already imposes. What is genuinely impossible
is *returning* one, since no `async` method may return a `ref struct`. So:

- *Low-level, zero-alloc:* `ValueTask<int> FillBufferAsync(ct)` fills the buffer, then a
  synchronous `bool TryNextRecord(out RecordRef)`. Both calls sit in the caller's own `async`
  loop; upstream's `fill_buf()` / `try_next_record()` pair ports 1:1. There is no
  `Task<RecordRef>` and there cannot be.
- *High-level, ergonomic:* `IAsyncEnumerable<T>` that copies each record out. Costs a copy;
  most users will not care and will reach for this first. (`yield` carries the same restriction
  as `await`, which is *why* it must copy.)

Ship both. Make the low-level one the documented path for latency-sensitive consumers.

**The bytes reach the buffer through `DbnFsm.SpaceMemory()`, not `Space()`** — a
`MemoryManager<byte>` owned by `AlignedBuffer` projects its `ulong[]` as a `Memory<byte>`, because
there is no `ReadAsync(Span<byte>)` and no `Memory<T>` reinterpret cast. Decided in #15; the full
rationale, including why the readiness-then-sync-read alternative fails under `compression=zstd`,
is in PORTING.md §1.

**Scalars.**
- Prices: `long`, scale `1e-9` (`FIXED_PRICE_SCALE = 1_000_000_000`), sentinel
  `UNDEF_PRICE = long.MaxValue`. Expose the raw `long` plus decimal/double helpers. Do **not**
  silently convert to `decimal` in the hot path.
- Timestamps: `ulong` UNIX nanoseconds, sentinel `UNDEF_TIMESTAMP = ulong.MaxValue`. Raw nanos
  are the primary representation on the wire and in every record struct; **NodaTime `Instant`
  and `LocalDate` are the representation everywhere above the codec**, converted through
  `DbnTime`, which checks the sentinel. The BCL date and time types are banned repo-wide and the
  build fails on them — a BCL tick is 100 ns and cannot represent a DBN timestamp at all. See
  CLAUDE.md, "Dates and times".
- `c_char` fields (`action`, `side`) — stored as `sbyte`, surfaced as `char` properties and
  as enums (`Action`, `Side`).
- `flags` — `[Flags]` enum over `byte` (`FlagSet`).
- Fixed-size C strings (symbols: 71 bytes in DBN v3) — C# 12 `[InlineArray]`, decoded to
  `string` lazily via a UTF-8 span read up to the NUL.

**Versioning.** DBN is at **version 3**, with v1 and v2 still readable. The Rust client models
this as `VersionUpgradePolicy` (`AsIs` / `UpgradeToV2` / `UpgradeToV3`, default `UpgradeToV3`)
and keeps separate `v1`/`v2`/`v3` struct namespaces. We need the same: decode older records
and upgrade them in-place to v3 shapes. Design this in from the start — retrofitting record
versioning is painful.

---

## 2. Milestone 0 — Foundation (prereq for everything)

> Tracked by [#1](https://github.com/jerbersoft/databentodotnet/issues/1) · milestone `M0: Foundation` ✅

- [x] Solution scaffold (`DatabentoDotNet.slnx`), `Directory.Build.props`, central package
      management, nullable + AOT/trim analyzers, deterministic builds, SourceLink, snupkg.
- [x] `global.json` with `rollForward: latestMajor` + `allowPrerelease`, so the repo builds on
      the .NET 10 GA SDK today and on a newer SDK without edits.
- [x] ~~**Conditional `net11.0` target.**~~ Removed in #16. `LibraryTargetFrameworks` is now
      plain `net10.0`; it used to be derived from the installed SDK major version, which is
      exactly the inference that let a missing preview SDK drop the target silently.
- [x] `nuget.config` pinning restore to nuget.org with `<clear />` — reproducible restore, and
      a public library can never resolve a package from a private feed.
- [x] `.editorconfig`; CA1707/CA1515 scoped off under `tests/**` for `Member_Scenario` naming.
- [x] CI (GitHub Actions) on Linux/macOS/Windows, on 10.0.x.
- [x] First vertical slice: `DbnConstants`, `RecordHeader`, the zstd seam, layout tests green.
- [x] Naming decided: `DatabentoDotNet.*` for package IDs, assemblies, namespaces, projects,
      and the solution. All five IDs confirmed available on 2026-08-26.
- [x] .NET 11 preview SDK intentionally **not** installed locally — and as of #16 the repo no longer has a target that needs it.
- [x] Claim the `DatabentoDotNet.*` IDs on nuget.org. Written as "publish placeholder packages";
      satisfied instead by publishing the real ones — `0.1.0-alpha`, then the `0.9.0` beta ([#74]).
      A placeholder would have claimed the same four names and taught nothing about the pipeline.

**Definition of done:** ✅ `dotnet build` green, `dotnet test` green (3/3), CI defined.

---

## 3. Milestone 1 — `DatabentoDotNet.Dbn` codec

**Status: complete** on branch `m1-dbn-codec` (24 commits, 789 tests, zero warnings),
with #16 (dropping the `net11.0` target) folded in, and #11 (numeric validators for `Publisher`,
`Dataset` and `Venue`) closed after it. Nothing in M1 is outstanding.

> Tracked by [#2](https://github.com/jerbersoft/databentodotnet/issues/2) (enums), [#3](https://github.com/jerbersoft/databentodotnet/issues/3) (records), [#4](https://github.com/jerbersoft/databentodotnet/issues/4) (metadata), [#5](https://github.com/jerbersoft/databentodotnet/issues/5) (decoder), [#6](https://github.com/jerbersoft/databentodotnet/issues/6) (symbol maps) · milestone `M1: DBN codec`

This is the critical path. Live streaming *is* DBN over a socket; nothing ships without it.

### 1a. Enums & constants
`RType` (0x00 Mbp0 … 0xC4 Bbo1M), `Schema` (0–19), `SType` (0–15+), `Compression`,
`Encoding`, `Action`, `Side`, `InstrumentClass`, `StatType`, `StatusAction`, `FlagSet`,
`VersionUpgradePolicy`. Plus the publisher/dataset/venue tables (generated — do not hand-write).

`RType` → record type dispatch is the decoder's core switch. Note `0x00..0x0F` encode MBP
book depth, so `Mbp0 = 0x00`, `Mbp1 = 0x01`, `Mbp10 = 0x0A` are depths, not arbitrary tags.

### 1b. Record structs + **size conformance tests**
Port every record with `[StructLayout(LayoutKind.Sequential)]`. The wire sizes below are
`static_assert`ed in `databento-cpp` — **assert `Unsafe.SizeOf<T>()` against each one.** This
test is the single highest-value test in the repo; it catches every layout mistake at once.

| Struct | Bytes | | Struct | Bytes |
|---|---|---|---|---|
| `RecordHeader` | 16* | | `OhlcvMsg` | 56 |
| `MboMsg` | 56 | | `StatusMsg` | 40 |
| `BidAskPair` | 32 | | `InstrumentDefMsg` | 520 |
| `ConsolidatedBidAskPair` | 32 | | `ImbalanceMsg` | 112 |
| `TradeMsg` | 48 | | `StatMsg` | 80 |
| `Mbp1Msg` | 80 | | `ErrorMsg` | 320 |
| `Mbp10Msg` | 368 | | `SymbolMappingMsg` | 176 |
| `BboMsg` | 80 | | `SystemMsg` | 320 |
| `Cmbp1Msg` | 80 | | | |
| `CbboMsg` | 80 | | | |

Also assert **alignment == 8** and **no interior padding** for each (the Rust suite does).
`MAX_RECORD_LEN = sizeof(WithTsOut<InstrumentDefMsg>)` = 520 + 8 = **528** — that is the read
buffer's minimum record capacity.

`RecordHeader` layout: `length: u8` (in **32-bit words**, so bytes = `length * 4`),
`rtype: u8`, `publisher_id: u16`, `instrument_id: u32`, `ts_event: u64`.

### 1c. Metadata header
Prelude is 8 bytes: magic `"DBN"` (3) + `version: u8` (1) + `length: u32 LE` (4).
Then a fixed section of `METADATA_FIXED_LEN = 100` bytes:
dataset cstr, `schema: u16` (`NULL_SCHEMA = u16::MAX` when absent), start/end/limit,
`stype_in: u8` (`NULL_STYPE = u8::MAX`), `stype_out: u8`, `ts_out: u8`,
`symbol_cstr_len: u16` (**v2+ only**), reserved padding, `schema_definition_length: u32` (always 0).
Then variable-length: `symbols`, `partial`, `not_found`, `mappings`.
`NULL_RECORD_COUNT = u64::MAX`. Version 0 means legacy DBZ — never emit it.

### 1d. Decoder / encoder
- Streaming, incremental decoder (the Rust one is an explicit FSM — mirror that; it handles
  partial reads at arbitrary boundaries, which a socket will absolutely produce).
- Zstd frame handling: `ZstdSharp.Port` behind `Internal/ZstdDecompressor.cs`.
- Encoders: DBN out, plus **CSV and JSON** (needed for tooling and for round-trip tests).
- v1/v2 → v3 upgrade path.

### 1e. Symbol mapping
`TsSymbolMap` / `PitSymbolMap` — instrument_id ↔ raw symbol resolution over time. Live streams
deliver `SymbolMappingMsg` records that must feed this incrementally.

Both implement `ISymbolIndex`, so a consumer resolves a symbol straight from a decoded record —
`map.TryGetSymbol(record, out var symbol)` — instead of assembling the `(instrumentId, date)` key
itself. That key is the easy thing to get silently wrong: most schemas index on `ts_recv`, not on
the `ts_event` every record has, and the two routinely fall on opposite sides of UTC midnight.
There is no indexer; a miss is expected, not exceptional. See PORTING.md §2.

**Definition of done:** decode every `.dbn` and `.dbn.zst` fixture in
`databento-rs/tests/data/` (mbo, mbp-1, mbp-10, tbbo, trades, ohlcv-1s/1m/1h/1d, definition,
imbalance, statistics) and round-trip re-encode byte-identically.

---

## 4. Milestone 2 — `DatabentoDotNet.Live` (**top priority**)

**Status: complete.** All six units landed — the mock gateway ([#18]), the connection ([#19]), CRAM
([#20]), subscriptions ([#21]), the session and record loop ([#22]), the reconnect pair ([#23]) —
plus the allocation measurement ([#28]) and the real-gateway smoke tests ([#25]).

Both definition-of-done items below are met, and the second of them is met in the strong sense:
the lifecycle test has actually been **run** against the real gateway, not merely written. The
first time it ran it failed, on an assertion of ours rather than on anything the client did
([#29]) — which is the entire argument for surface (2) existing, restated as evidence.

> Tracked by [#10](https://github.com/jerbersoft/databentodotnet/issues/10) · milestone `M2: Live streaming`

### Gateway
`{dataset.ToLower().Replace('.', '-')}.lsg.databento.com:13000` — e.g.
`GLBX.MDP3` → `glbx-mdp3.lsg.databento.com:13000`. Plain TCP.

Landed as `LiveGateway.For` in [#19], asserted for every one of the 52 `Dataset` values rather
than spot-checked, with a sweep that fails if regenerating the publisher tables adds a dataset
the host table does not cover. The dataset is **not** validated against that enum — Databento
ships datasets faster than a generated table tracks them, and refusing a dataset that exists
because our table is stale is worse than a DNS error. Only the shape of the resulting DNS label
is checked. See PORTING.md §2.

[#19]: https://github.com/jerbersoft/databentodotnet/issues/19

### Session lifecycle
1. **Connect** TCP. *(#19: `LiveClient.ConnectAsync`, connect budget, resolved endpoint retained
   for reconnect. A refused port raises `LiveConnectException`; only an elapsed budget raises
   `ConnectTimeoutException`.)*
2. **Greeting** — read one `\n`-terminated line (`lsg_version=…`).
3. **Challenge** — read one line, must start with `cram=`; the remainder is the challenge.
4. **Auth reply** — `SHA256(challenge + "|" + apiKey)`, lowercase hex, then send one line:
   ```
   auth={sha256_hex}-{bucket_id}|dataset={ds}|encoding=dbn|compression={c}|ts_out={0|1}|client={ua}[|heartbeat_interval_s=N][|slow_reader_behavior={warn|skip}]\n
   ```
   `bucket_id` = **last 5 chars of the API key**. Keys are **32 ASCII chars** — validate, and
   reject the literal `$YOUR_API_KEY` placeholder with a clear message.
5. **Auth response** — one line of `|`-delimited `k=v`. Require `success=1`; otherwise raise
   with the `error=` value. Capture `session_id`.

   *(Steps 2–5 landed as `LiveClient.AuthenticateAsync` in [#20], separate from `ConnectAsync`
   so each timeout is nameable as the failure it is. The digest is asserted against a constant
   computed outside this codebase, not against the library's own SHA-256 call, and the mock
   gateway recomputes it rather than checking it is hex — a client that transposes the challenge
   and the key fails in the suite, not against a real gateway. Upstream has no budget on the
   handshake at all; `AuthTimeout` covers the whole exchange and cancels by tearing the socket
   down, because a half-written control line desynchronises the gateway. A challenge without the
   `cram=` prefix raises `LiveProtocolException`, deliberately not
   `DatabentoAuthenticationException`: hashing an empty challenge yields a digest the gateway
   rejects, and reporting that as a bad key sends the caller to rotate credentials that were
   never at fault. See PORTING.md §2.)*
6. **Subscribe** (repeatable, pre- or post-start):
   ```
   schema={s}|stype_in={t}|symbols={csv}|snapshot={0|1}|is_last={0|1}[|start={unix_nanos}][|id={n}]\n
   ```
   **Chunk symbols at 500 per message**; only the final chunk sets `is_last=1`.
   `snapshot=1` and `start` are **mutually exclusive** — validate client-side.

   *(Landed as `LiveClient.SubscribeAsync` in [#21], with `Symbols` and `Subscription`. The 500
   boundary is asserted at 1, 499, 500, 501, 1000 and 1001 symbols, against the mock gateway and
   again in isolation — a client that chunked at 501 and a gateway that accepted 501 would agree
   with each other and both be wrong. Three client-side rejections, each proved to write nothing
   by the gateway then reading an ordinary subscription as the first line it ever saw:
   `snapshot` with `start`, `snapshot` on any schema but MBO, and a `Symbols` that names nothing.
   Two departures from upstream, both documented in PORTING.md §2 — an empty symbol list is
   rejected where upstream underflows `len() - 1`, and an exhausted id counter throws where
   upstream warns and then hands out a duplicate. The line format is confirmed against the real
   gateway by an opt-in smoke test that costs nothing, because subscriptions travel before
   `start_session` and no data moves until it is sent.)*
7. **Start** — send `start_session\n`. The gateway then emits DBN metadata followed by the
   record stream.

   *(Landed as `LiveClient.StartAsync` in [#22], with the record loop behind it. **This is the
   line that begins billing** — everything above it moves no market data, which is what made the
   [#25] smoke tests free and what makes the one test that crosses it carry a second opt-in of its
   own. `ts_out` is taken from the metadata block rather than from what the client asked for: the
   two are different facts, and only the second changes every record's length. A `compression=zstd`
   session gets its decompressor inserted at exactly this byte, which is why the control channel
   reads the socket one byte at a time — a buffered reader would already have swallowed the front
   of this metadata while reading the auth response. Every record the mock gateway sends is
   asserted to decode byte-identically to the same bytes through `DbnDecoder`, in all four
   combinations of {plain, zstd} × {`ts_out` off, on}.)*

[#20]: https://github.com/jerbersoft/databentodotnet/issues/20
[#21]: https://github.com/jerbersoft/databentodotnet/issues/21
[#23]: https://github.com/jerbersoft/databentodotnet/issues/23

### Client surface
Mirror `databento-rs`: `ConnectAsync`, `Subscribe`, `StartAsync` (returns `Metadata`),
`FillBufferAsync`, `TryNextRecord`, `ReconnectAsync`, `ResubscribeAsync`, `CloseAsync`,
plus a builder. `ReconnectAsync` + `ResubscribeAsync` as **separate** operations is a
deliberate upstream choice — replaying subscriptions is a caller decision, not automatic.

Upstream's `next_record()` is deliberately **absent**: it returns a `RecordRef<'_>` from an
`async fn`, which C# cannot express at all. `FillBufferAsync` + `TryNextRecord` is the port of
its `fill_buf()` / `try_next_record()` pair, and the ergonomic `IAsyncEnumerable<T>` (which
copies) is what most callers will use instead. See #15.

*(All of this has landed — the read loop in [#22], the reconnect pair in [#23]. The
`IAsyncEnumerable<T>` is
`RecordsAsync`, yielding `OwnedRecord` — a heap copy, because `yield return` carries the same
restriction `await` does and a `ref struct` cannot leave an iterator at all. Its price is
measured rather than asserted away: two allocations per record, against zero for the pair it is
written in terms of. **Two departures from upstream are worth knowing about.** `FillBufferAsync`
splits into a synchronous fast path and an async slow one, because building a
`CancellationTokenSource` and a registration on every call is three allocations that a read the
socket can already satisfy has no reason to pay — without that split the zero-allocation target
is not reachable at all, which is the kind of thing [#28] exists to discover. And cancelling it
ends the session where upstream's is cancel-safe: tokio guarantees a dropped read consumed
nothing and .NET makes no such promise about a socket, so a cancelled fill marks the client
closed rather than resuming mid-record. PORTING.md §1.)*

### Details that bite
- **Heartbeats** arrive as `SystemMsg` records, not control frames. `heartbeat_interval_s` is
  opt-in. Use it as a liveness signal and surface a configurable read timeout.
  *([#22] landed the read timeout and `HeartbeatTimeoutException` with the loop that raises them,
  since the liveness check belongs in the read loop and not in a parallel timer that could report
  a timeout while records were arriving. `ReadTimeout` overrides, and `EffectiveReadTimeout`
  derives upstream's `heartbeat_interval + 5s`, or 35s when no interval was requested. The name
  stays upstream's rather than becoming `ReadTimeoutException`, because the name is the
  explanation: silence is only evidence of a dead connection because the gateway promises to send
  a heartbeat when nothing else is due. Without that promise, 35 quiet seconds at 3am would be
  ordinary and no read timeout could be justified at all. [#23] then closed the other half: a
  heartbeat replayed between two records proves it is framed like any other record and leaves the
  stream in step, and a gateway that goes quiet with only a `HeartbeatInterval` configured is
  asserted to give up at `interval + 5s` — the arithmetic was already checked, but not that the
  number it produces is the one the read actually runs on, which is the number a deployment lives
  or dies by.)*
- **`SlowReaderBehavior`** — `Warn` (gateway warns, keeps sending) or `Skip` (gateway drops
  records to catch you up). Expose it; a slow .NET consumer is a realistic failure mode.
  *([#23] — on the auth line, asserted in both settings and asserted absent when unset. Both
  settings, because the two mean opposite things to the gateway and a client that sent one
  spelling for both would be silently choosing for every caller.)*
- **`ts_out`** appends an 8-byte gateway send-timestamp to every record. When enabled, record
  length changes — the decoder must know. This is `WithTsOut<T>`.
- **Cancellation is not safe mid-handshake.** Upstream documents auth and subscribe as
  *not* cancel-safe: a partially written message desyncs the gateway and it closes the
  connection. In .NET terms, do not thread a `CancellationToken` naively into the middle of
  those writes — cancel by tearing down the socket, not by abandoning a partial write.
- **Intraday replay** via `start` on a subscription — replays from a timestamp, then
  transitions to live. Same socket, no separate code path.

### Test harness — landed first, on purpose
`MockLiveGateway` in `tests/DatabentoDotNet.Live.Tests` ([#18]) is the port of upstream's
`MockGateway`, and it shipped before any of the client. Nothing else in M2 is testable without
it, and a harness grown inside whichever issue happened to need it first would be shaped by that
one caller — every later issue then bending its tests around that shape.

It speaks the gateway half over a real loopback socket: greeting, a fixed CRAM challenge, the
auth line (digest *verified*, not merely checked for hex), chunked subscriptions, `start_session`,
metadata, and records — plain or zstd-framed, with or without `ts_out`. Every rejection is a
`MockGatewayException` naming the line that caused it, and its own tests drive it with a
deliberately malformed client to prove those rejections fire. `StubLiveClient`, written from
`live/protocol.rs` rather than from the gateway, is the second opinion that keeps the two honest
before a real client exists. See PORTING.md §2 for what changed on the way across.

[#18]: https://github.com/jerbersoft/databentodotnet/issues/18

**Definition of done — two surfaces, because only one of them can be bought.**

1. **Allocation, measured against the mock.** `MockLiveGateway` replays a synthetic MBO stream
   over its loopback socket, and the `FillBufferAsync` / `TryNextRecord` path allocates zero
   managed bytes per record once warm. *Measured* — a benchmark and a suite assertion ([#28]) —
   not concluded by reading the code.
2. **Protocol, exercised against the real gateway.** One test runs the whole lifecycle against
   `DATABENTO_LIVE_DATASET` on **a schema this account holds a live license for** — connect,
   authenticate, subscribe, `start_session`, the metadata block, a bounded handful of records,
   close — confirming that our reading of the wire format matches the gateway's. It needs an
   opt-in of its own *on top of* `Category=Live` ([#25]), because it is the only test in the
   repo that moves billable data; building that second gate belongs to [#22].

> Amended by [#27]. This previously read "…plus a live smoke test against a real dataset,
> sustaining an MBO stream with zero per-record allocation on the low-level path" — one surface
> asked to do both jobs. **No dataset this account licenses offers `mbo`**, so no issue in M2
> could satisfy that clause, and a target nothing can work toward is worse than no target — the
> same argument that produced [#26] against CLAUDE.md.
>
> **Synthetic MBO is not a climbdown, because allocation is a property of the code path and not
> of the data source.** Bytes land in `AlignedBuffer`, are reinterpreted in place by
> `MemoryMarshal.AsRef<T>`, and reach the caller as a `RecordRef`. Nothing on that path can tell
> a real gateway from a mock replaying a synthetic `MboMsg`, so measuring against the mock gives
> up nothing that matters — and buys a measurement that runs in CI at 3am on a Sunday rather
> than only during market hours on a feed we would have to purchase first. What real data buys
> is *protocol* confidence, which is what surface (2) is for. Upstream measures the same way:
> its own MBO tests run against its mock gateway, not against a live subscription.
>
> MBO is chosen for surface (1) despite being synthetic because it is the densest schema DBN
> defines — the hardest case for a per-record allocation claim, and the one a reader would
> otherwise suspect we avoided.
>
> **Surface (2) does have to start a session, which is why it is gated twice.** `MockLiveGateway`
> and the client were written from the same reading of `live/protocol.rs`, so the mock cannot
> independently confirm the metadata block or the record framing — a misreading shared by both
> agrees with itself, and `StubLiveClient` is a second opinion from the same source rather than a
> second source. Only a real gateway settles it, and only after `start_session`. The [#25] smoke
> tests stop short of that line deliberately, because until [#22] nothing in the client could read
> what came back; from [#22] the rule becomes *no test starts a session without its own opt-in*
> rather than *no test ever starts one*.
>
> **Licenses as of 2026-08**, since the split turns on them: `EQUS.MINI` (`mbp-1`, `tbbo`,
> `trades`, `bbo-1s`, `bbo-1m`, `ohlcv-1s`/`-1m`/`-1h`/`-1d`, `definition`) and `EQUS.SUMMARY`
> (`ohlcv-1d`, `definition`, `statistics`). `mbo` is a venue-feed schema — `XNAS.ITCH`,
> `GLBX.MDP3`, `DBEQ.BASIC` — and none of those are licensed. Surface (2) deliberately names the
> *entitlement* rather than a fixed schema, so this list going stale does not invalidate the
> target; `.env.example` and `LiveCredentials.DefaultDataset` carry the operational half.

[#22]: https://github.com/jerbersoft/databentodotnet/issues/22
[#25]: https://github.com/jerbersoft/databentodotnet/issues/25
[#26]: https://github.com/jerbersoft/databentodotnet/issues/26
[#27]: https://github.com/jerbersoft/databentodotnet/issues/27
[#28]: https://github.com/jerbersoft/databentodotnet/issues/28
[#29]: https://github.com/jerbersoft/databentodotnet/issues/29

---

## 5. Milestone 3 — `DatabentoDotNet.Historical`

> Tracked by [#7](https://github.com/jerbersoft/databentodotnet/issues/7) · milestone `M3: Historical`

Base URL `https://hist.databento.com`, paths `v0/{slug}` (`API_VERSION = 0`), **HTTP Basic auth with
the API key as the username and an empty password**. Honour the `X-Warning` response header
(surface as warnings) and log `request-id` on every error — it is what support will ask for.

### The split

[#7] is decomposed the way [#10] was, into nine issues whose order is the dependency order rather
than a preference:

| Issue | What it delivers | Depends on |
|---|---|---|
| [#32] | Move `Symbols`, `ApiKey` and `UserAgent` out of `DatabentoDotNet.Live` | — |
| [#33] | `DateRange` and `DateTimeRange` in NodaTime, and their two wire renderings | — |
| [#34] | Mock historical gateway harness | — |
| [#35] | Project, Basic auth, URLs, errors and warnings | [#32], [#34] |
| [#36] | `metadata.*` | [#33], [#35] |
| [#37] | `symbology.resolve`, and its probed exclusive `end_date` | [#33], [#35] |
| [#38] | `timeseries.get_range` and `get_range_to_file` | [#33], [#35] |
| [#39] | `batch.*` | [#33], [#35] |
| [#44] | Real-API harness, and free coverage of `metadata.*` | [#36] |
| [#45] | `get_dataset_condition`'s inclusive `end_date`, converted at its renderer | [#44] |
| [#40] | Opt-in tests against the real historical API | [#37]–[#39], [#44] |

[#34] goes before [#35] for the reason [#18] went before [#19]: nothing below is testable without a
harness, and a harness grown inside whichever issue happens to need it next is a harness shaped by
one caller.

[#44] is [#40] pulled forward as far as it will come. [#40] depends on all four clients, and waiting
for them would have left ten shipped endpoints uncalled while [#37]–[#39] each copied
`MetadataClient`'s shape — so a wrong shape would have been inherited three more times before anyone
found out. The free half needs only [#36], costs nothing to run, and it found [#45] on its first
pass. [#40] keeps what genuinely needs the other three: the billable `get_range` that proves the
reading, and the `batch` job lifecycle. `symbology.resolve` left that list at [#37], which found its
endpoint free to call and so brought its own real-API coverage rather than deferring it.

Endpoints, grouped as upstream does:

- **`timeseries.get_range`** — the main event. Streams DBN over HTTPS; reuse the M1 decoder
  directly. Also `get_range_to_file`. *([#38].)*
- **`metadata.*`** — `list_publishers`, `list_datasets`, `list_schemas`, `list_fields`,
  `list_unit_prices`, `get_dataset_condition`, `get_dataset_range`, `get_record_count`,
  `get_billable_size`, `get_cost`. *([#36].)*
- **`symbology.resolve`**. *([#37].)*
- **`batch.*`** — `submit_job`, `list_jobs`, `list_jobs_full`, `get_job_details`,
  `list_files`, `download`, `download_file`. *([#39].)*

Notes: cost/billing endpoints should be prominent in docs — users want `get_cost` *before*
`get_range`. Batch download needs resumable, parallel file transfer and integrity checks.

### Three decisions the split surfaced

Each is owned by the issue that has to make it; recorded here because each one is invisible in six
months and each one is a departure from either upstream or from the obvious .NET answer.

**1. `Symbols`, `ApiKey` and `UserAgent` have to leave `DatabentoDotNet.Live` ([#32]).** Upstream has
one crate and so has no version of this problem — all three sit in `lib.rs`, above both transports.
Splitting by package forces the choice, and **linking the files the way `Internal/ZstdDecompressor.cs`
is linked does not work**: that file is `internal`, so compiling it into two assemblies produces two
types nobody outside can name. `Symbols` is public, so linking it would give a consumer holding both
packages `DatabentoDotNet.Live.Symbols` *and* `DatabentoDotNet.Historical.Symbols` — one name, two
types, and no way to pass one to the other. The recommendation on that issue is
`DatabentoDotNet.Dbn`, on the ground that a package nobody can use without also taking the codec is
not really a separate package.

**2. The HTTP test double is a real server, not an `HttpMessageHandler` stub ([#34]).** The stub is
the reflexive .NET choice and it is ruled out by this milestone's own definition of done rather than
by taste: it never opens a socket, so it cannot exercise chunked transfer and back-pressure — which
is the whole of "flat memory" — nor a `Range: bytes=N-` answered with `206 Partial Content`, which
is the whole of "resumable across a process restart", nor a connection dropped mid-body, nor
`HttpClient` itself, which is the component under test in half of M3. `MockLiveGateway` speaks over
a real loopback socket for the same reason. Upstream's `wiremock` is a Rust ecosystem fact rather
than a design argument; what CLAUDE.md says to port is the harness's *behaviour*.

**3. `get_range_to_file` cannot be ported as upstream writes it ([#38]).** Upstream decodes the
response and **re-encodes** it to disk with `AsyncDbnEncoder`, applying the upgrade policy on the
way through. **There is no record encoder in this library and deliberately will not be one**
(CLAUDE.md, "Testing"), so that route is closed — and it is also unnecessary, because the response
body already *is* zstd-framed DBN and writing it is a byte copy. No decode, no re-encode, nothing to
get wrong in between, and the file that lands is bit-identical to what the API served.

> The behavioural difference is worth stating rather than leaving to be discovered. Upstream's file
> holds records at the *upgraded* version and is therefore read back with `AsIs`; ours holds them at
> the version the API sent, and the upgrade policy applies when the file is read. Ours is the more
> defensible of the two — a cached response that is not what the server sent is a cache that can lie
> — but it is a difference, and it belongs in the method's doc comment.

**Definition of done:** every endpoint covered, plus the two things that only exist at the seam
between the sub-issues, each stated as the measurement that settles it rather than as an intention:

- **`get_range` streams a range far larger than any buffer with flat memory** ([#38]), measured with
  `GC.GetAllocatedBytesForCurrentThread()` in the style of `AllocationTests` and
  `LiveAllocationTests` — including the companion test that proves the instrument would notice a
  deliberate allocation. Per-record cost that does not grow with the range is the property that
  makes multi-GB work, and unlike a multi-GB download it runs in CI in seconds.
- **Batch download resumes across a process restart** ([#39]), proved by interrupting a transfer
  against the harness and byte-comparing the result — not by observing that a `Range` header was
  sent.

Two departures from upstream on the download path, both on [#39]: a checksum mismatch **throws**
where upstream logs a warning and returns success, and files transfer in parallel where upstream's
`download` loops sequentially.

> Porting them found a third thing, which is why the first departure needed it: upstream builds one
> hasher outside its retry loop and re-reads the partial file into it on every attempt, so **after
> any retry the digest cannot match**. Harmless upstream, where a mismatch is a warning; fatal here,
> and on exactly the resumed transfers this milestone is about. PORTING.md §4 has that and the other
> four findings [#39] measured against the live API — including that the batch API knows seven job
> states where upstream's enum has four, which breaks a whole listing rather than one element.

### Seven decisions made during implementation

The split above recorded three decisions as *questions the sub-issues would have to answer*,
written before any of [#32], [#33] or [#34] had a line of code. All three are now implemented,
reviewed and merged, and each answered its question — sometimes exactly as predicted, sometimes
with specifics the split couldn't have known. A fourth decision, made by the controller during
review rather than by any single issue, belongs alongside them; a fifth is the wire-accessor
naming rule settled by [#42]; a sixth is the HTTP transport [#35] put underneath every endpoint
that is still to come; and a seventh is the shared parameter type and `decimal` money that [#36]
settled for the first group of endpoints to sit on that transport.

**Where the shared types went ([#32]).** `Symbols`, `ApiKey` and `UserAgent` move out of
`DatabentoDotNet.Live` into `src/DatabentoDotNet.Dbn/Common/`, under the root namespace
`DatabentoDotNet` rather than `DatabentoDotNet.Dbn` — the codec project's own `RootNamespace`
stays `DatabentoDotNet.Dbn` unchanged, and a file declaring `namespace DatabentoDotNet;` inside it
is deliberate, not a slip. As predicted above, linking the files the way
`Internal/ZstdDecompressor.cs` is linked was rejected: that precedent works only because
`ZstdDecompressor` is `internal`, so compiling it into two assemblies produces two types nobody
outside either assembly can name — no ambiguity to create. These four types are `public`; linking
them would compile `DatabentoDotNet.Live.Symbols` and `DatabentoDotNet.Historical.Symbols` as two
distinct CLR types sharing one name, and a consumer holding both packages could not pass a
`Symbols` built for one transport to the other. `Symbols` also gained a second rendering,
`ToApiString()` — every symbol comma-joined with no chunking, porting upstream's
`Symbols::to_api_string()` — alongside the existing `ToChunks()`, which splits at 500 symbols
because that's a live line-protocol limit an HTTP form field doesn't share; upstream never faces
this choice, because it has one crate and splitting by NuGet package is what forces it here. This
is M3's only breaking change pre-1.0 (the `breaking-change` label tracks it rather than a version
bump): `DatabentoDotNet.Live.Symbols`, `.ApiKey` and `.UserAgent` no longer exist, and a consumer
updates a `using` and nothing else.

**Empty and inverted ranges are rejected at construction, not sent to the API ([#33]).**
`DateRange` and `DateTimeRange` (`src/DatabentoDotNet.Historical`) require their exclusive `End`
strictly after their inclusive `Start`; every named factory — `OnDay`, `Between`, `Including`,
`Spanning`, and `DateTimeRange.FromUnixNanoseconds` — throws `ArgumentException` for an empty or
inverted pair rather than build it. The rejected alternative is upstream's own behavior: send
whatever the caller constructed and let `hist.databento.com` answer, undocumented, on its own
time and its own bill. The precedent is `Symbols`' own rejection of a malformed symbol ([#21]) —
reject while the offending value is still in the caller's hand, rather than after a network round
trip a query-parameter mistake didn't need — sharpened here because a date range is frequently
*computed* (`end = start + someDuration`, or two variables swapped), exactly the class of mistake
that's cheap to catch locally and expensive to catch by billed round trip. One upstream test
pulls against this: `date_range_from_lt_day_duration` asserts that a sub-day `Duration` produces a
silently *empty* `DateRange`, because `time::Date + time::Duration` truncates to whole days. This
port's `Spanning` runs the identical truncation but then validates the result like every other
factory, so the same call **throws** here instead of succeeding empty — a deliberate, pinned
divergence (`DateRange_Spanning_SubDayDuration_Throws`), chosen over carving one factory out of an
otherwise uniform rule. Review surfaced a related gap: a struct's implicit parameterless
constructor can't be suppressed, so `default(DateRange)` and `default(DateTimeRange)` bypassed
validation entirely, silently rendering a plausible-looking `"0001-01-01"` from a range that was
never actually constructed. Both types now guard their four wire-render accessors with an
`EnsureUsable()` check that throws `InvalidOperationException` — not `ArgumentException`, since
there's no bad argument at a property getter, just an instance that never went through a
factory — matching the narrow-guard shape `Symbols` already uses for its own `SymbolsKind.None`
default.

**One endpoint's inclusive `end_date` is converted at its own renderer, not modelled in the type
([#45]).** `metadata.get_dataset_condition` closes its date range at both ends, so the half-open
`DateRange` every other endpoint takes asked it for n days and was answered for n + 1 —
`DateRange.OnDay(d)` reported on `d` and `d + 1`, which is a named factory breaking its own promise.
`GetDatasetConditionParams.ToQueryParameters` now sends the day before the exclusive `End`, so a
caller's range means the same thing at every endpoint in the library. **The rejected alternative was
a second, public, closed-range type for this one endpoint**, which would have put the difference in
the caller's source rather than in a renderer — honest, and priced at a type every caller must
choose between forever to describe one server's reading of one parameter. What settled it was
probing the other endpoint that shares the type: upstream documents nothing about `list_datasets`'
end (`metadata.rs:41-50`), and against the real API it is genuinely half-open. So the difference belongs to
`get_dataset_condition` rather than to the library's model of a date range, which is the argument
for rendering it at that endpoint and for not moving the type — and the tempting fix, converting
inside the single shared renderer [#36] had just consolidated, would have broken `list_datasets`
with nothing in the suite to say so. Both renderers are now named for the wire contract they produce
(`ToExclusiveEndDateParameters`, `ToInclusiveEndDateParameters`), so a future endpoint cannot pick
the wrong one by not choosing, and both readings are pinned by a real-API test. This is a deliberate
divergence from every other Databento client: upstream's `DateRange` is half-open too
(`From<RangeInclusive>` normalizes with `next_day()`, `historical.rs:72-79`) and its one
`AddToQuery` sends `end` verbatim everywhere, so `databento-rs` carries the identical off-by-one and
documents the consequence at the field instead of correcting it; `databento-cpp`'s `DateRange` is a
pair of raw strings and offers no opinion at all. It costs nothing to correct, because a caller who
wants `d` and `d + 1` writes `DateRange.Including(d, d.PlusDays(1))`.

**`symbology.resolve`'s `end_date`, asked rather than assumed ([#37]).** [#45] closed with a rule —
probe the endpoint you are about to change, not the one next to it — and [#37] is the first issue to
apply it before writing anything. `symbology.resolve` is the third call site to choose between
`DateRange`'s two renderers, and upstream documents an exclusive end for it (`symbology.rs:78`),
exactly as the docs around `get_dataset_condition`'s neighbours did before the real API contradicted
them. The endpoint is free, so asking cost nothing.

It is exclusive, and the decisive evidence was not a returned interval but a rejection: the server
refuses `start_date == end_date` with HTTP 422 `data_date_range_start_on_or_after_end`. An endpoint
that reads `end_date` as inclusive *must* accept that, because it is how such an endpoint spells a
single day — `get_dataset_condition` does. So the rule now has two outcomes behind it rather than
one, and "same shape, different answer" is established as the normal case rather than [#45]'s
anomaly.

**The same probe corrected three claims in [#37]'s own porting notes**, each of which would have
shipped as a design comment resting on something false. The response *does* carry `stype_in` and
`stype_out`, so echoing them from the request is a choice defended on its merits rather than the
only option. Every requested symbol appears in `result` — a not-found one as an empty array — which
generalises the issue's definition of done: `ContainsKey` answers "did I ask for this", never "did
this resolve". And a resolution in which nothing resolved arrives as HTTP 200 with `"status": 2`, so
`NotFound` is the only signal a caller ever gets.

**It also found a defect in an already-shipped doc comment.** `MetadataQueryParams` claimed to be
"the same set `timeseries.get_range` takes", and [#38]'s scope repeated the promise. Upstream keeps
two distinct types: `GetRangeParams` carries a `stype_out` (`timeseries.rs:189`) that
`GetQueryParams` does not, and posts it with `encoding` and `compression`
(`timeseries.rs:131-134`). None of the three changes a price, so their absence from the billing type
is correct — the promise to [#38] was not. `ResolveParams.FromQuery` therefore takes `stype_out` as
a required argument rather than defaulting it, because a resolution requested in the wrong output
symbology fails nowhere: every symbol resolves, no bucket fills, and the names are simply wrong.

**Kestrel, over `WireMock.Net` and `HttpListener` ([#34]).** M3's test double had to be a real HTTP
server on a loopback port; an `HttpMessageHandler` stub was never a candidate, disqualified by
this milestone's own definition of done rather than by taste. It never opens a socket, so it can't
exercise chunked transfer and back-pressure (the whole of "flat memory"), can't answer a
`Range: bytes=N-` with `206 Partial Content` (the whole of "resumable across a process restart"),
can't drop a connection mid-body, and it bypasses `HttpClient` itself, which is the component
under test in half of M3. Among real servers, Kestrel won on being in the box: it arrives as
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` — a shared framework the SDK ships
with, not a package — so `Directory.Packages.props` gains nothing and no per-RID asset appears
anywhere. `WireMock.Net` was rejected as a third-party dependency wrapping this same server plus a
matcher DSL the harness would use for little more than "match this path, return this body," and
whose per-test `given(…)` style makes the credential check something each test opts into, where
the point here is that no test *can* opt out of it. `HttpListener` was rejected as the legacy
stack: no new dependency, but the weakest streaming and `Range` story of the three. Upstream's own
choice of `wiremock` is a Rust ecosystem fact, not a design argument — what CLAUDE.md says to port
is the harness's *behavior*, not its package list.

**Where a mock gateway's bytes come from — a controller ruling, not an issue's ([#34]).** The
initial ruling was that `tests/DatabentoDotNet.Historical.Tests` references no `src/` project it
exists to test, and above all not the codec. That was challenged in review against the repo's own
precedent:
`MockLiveGateway`'s own csproj says it "needs the codec only … and deliberately does not use the
client it exists to test," which permits a codec reference. The challenge was resolved by
*keeping* the no-reference rule, but for a better reason than the original one: if a test served a
response body built by `MetadataEncoder` and then decoded it with `DbnDecoder`, a mistake in this
repo's own reading of the DBN metadata block would sit on both sides of the test, and the two
would agree with each other. The bytes a harness serves have to come from an independent oracle.
The vendored corpus in `tests/DatabentoDotNet.Dbn.Tests/Data/` is Databento's own output, with
record counts pinned to upstream's numbers, so an issue that needs a full DBN stream serves a
fixture from there through `MockHistoricalResponse.Binary` — which already accepts arbitrary
bytes and needs no project reference. This is the same argument CLAUDE.md already makes for
`MockLiveGateway` under "Testing" — "the mock cannot confirm what it shares an author with" — [#34]
just arrived before a historical client existed to reference at all. One consequence:
`SyntheticDbnFragment`, this project's other test data, serves a fragment with **no metadata
block**, deliberately stamped with an unassigned `rtype` (`0xFF`) so it can't be mistaken for a
real record — it exists to be transported, not decoded. The ruling also settles how [#38] gets a
real DBN stream once it needs one: serve a vendored fixture through `MockHistoricalResponse.Binary`,
not a hand-built metadata block.

**Wire accessors are named for the encoding they render, not the wire parameter, and
`From(x, Duration)` becomes `Spanning(x, Duration)` on both types ([#42]).** `DateRange` and
`DateTimeRange` named the same concept two different ways: `DateRange`'s `StartDate`/`EndDate`
after the wire parameters `start_date`/`end_date`, `DateTimeRange`'s
`StartUnixNanoseconds`/`EndUnixNanoseconds` after the unit rather than the wire parameters
`start`/`end` — because `Start`/`End` already name that type's own `Instant` properties. A
convention one of the two types structurally cannot follow is not a convention, so `DateRange`'s
accessors rename to `StartIsoDate`/`EndIsoDate`: `Start`/`End` plus the name of the wire encoding,
a rule both types can state and both now follow — `DateTimeRange`'s accessors were already correct
under it. The rejected alternative was naming for the wire parameter throughout, which is what
`DateRange` already did; it was rejected because `DateTimeRange` cannot follow it without
colliding with its own `Start`/`End` properties, and because `StartDate` does not say it hands
back a preformatted string rather than a `LocalDate`, while `StartIsoDate` does. Separately,
`DateRange.From(LocalDate, Duration)` becomes `DateRange.Spanning(LocalDate, Duration)`, and
`DateTimeRange.From(Instant, Duration)` becomes `DateTimeRange.Spanning(Instant, Duration)` —
applied to both types even though the issue named only `DateRange`, because the issue's own
closing requirement is that the two types agree afterwards. `From` in .NET names a conversion from
another representation — `Duration.FromHours`, `Instant.FromUnixTimeTicks`, this library's own
`DateTimeRange.FromUnixNanoseconds` — and `From(start, duration)` is not a conversion, it is a
span with an origin. `Spanning` says that and joins the shape-describing family
`OnDay`/`Between`/`Including`. The rejected alternative was leaving `From` as it stood, ruled out
for colliding conceptually with what `FromUnixNanoseconds` actually means; `FromUnixNanoseconds`
itself keeps its name unchanged, since it is a conversion and that is exactly what `From` means in
.NET.

**The transport every endpoint will sit on, settled before any endpoint exists ([#35]).** What
shipped is `HistoricalClient` — `PathFor`, `SendAsync`, the two readers `ReadJsonAsync` and
`ReadZstdJsonLinesAsync`, and their composed forms `SendJsonAsync` and `SendZstdJsonLinesAsync` —
alongside `HistoricalGateway`, `DatabentoApiException` and `Internal/HistoricalLog`. Endpoints
group into subclient facades — `client.Metadata.…`, `.Timeseries`, `.Symbology`, `.Batch` — and
this issue **declares none of them**. That shape is upstream's own — its four subclients at
`historical/client.rs:102-118`, which its own doc comment presents as how individual API methods
are reached (`:25-29`) — and it ports for its own sake rather than for the borrow checker's, which
is the only reason upstream's take `&mut self`. The rejected alternative is every endpoint flat on
one class, and it is worth naming that this is what databento-cpp does: one `Historical` class
(`include/databento/historical.hpp:26`) whose methods carry the group as a prefix instead —
`MetadataListDatasets`, `BatchSubmitJob`, `SymbologyResolve` — divided only by comment banners.
That is a prefix doing a namespace's job by hand, and it is not a surface anyone can navigate. But
a facade with no endpoints on it is a public empty class, so each of
[#36]–[#39] brings its own along with the first endpoint that goes on it, and what this issue owed
was the decision plus a transport they can call. That transport is **`public`, where upstream's
`get` (`historical/client.rs:125`) and `post` (`:140`) are `pub(crate)`** — forced, not chosen: the
definition of done requires that a request to *any* slug arrive at `v0/{slug}`, which only a public
method lets a test project drive, and this repo declares no `InternalsVisibleTo`, so "internal but
tested" is not a shape available here. It doubles as the escape hatch for an endpoint this library
has not wrapped yet, and [#38] needs it regardless, because `timeseries.get_range` reads a DBN byte
stream neither reader covers. **The signature is frozen; [#36]–[#39] are written against it.** Two
details of it were not free choices: `accept` precedes `cancellationToken` because CA1068 makes the
other order a build error under `TreatWarningsAsErrors` — with the side benefit that a token passed
positionally into the `accept` slot is now a compile error rather than a request that quietly
cannot be cancelled — and both readers are `static` because CA1822 fires on a public member that
touches no instance state, which neither does.

The API's `X-Warning` header **surfaces through `ILogger` and through nothing else**.
`ILoggerFactory? LoggerFactory { get; init; }` defaults to `null`, which resolves to
`NullLogger.Instance` — no logging configured means no logging done, and nothing is formatted for a
caller who never asked. The rejected alternative is a warnings property on the response, and it
lost on cost rather than on taste: it means every endpoint in the API returns a wrapper type
instead of its payload, and every caller unwrapping, to carry a header that is almost always
absent. Upstream faces no such choice — it logs through `tracing`
(`historical/client.rs:243`, `:247`) and returns the payload unwrapped. This costs one package
reference, `Microsoft.Extensions.Logging.Abstractions`: the standard .NET abstraction, AOT-clean,
and inert when nothing is configured. Messages are source-generated `[LoggerMessage]` partials in
`Internal/HistoricalLog.cs` with stable event ids, per PORTING.md §2 — which also now carries the
rule deciding which of upstream's `tracing` sites port at all (this library logs only what the
caller cannot otherwise see) and the two it rules out.

`HttpClient.Timeout` is set **infinite**, and every budget is a `Duration` on a linked
`CancellationTokenSource` instead. The default is 100 seconds and covers the *whole* operation
including reading the body, so a default-configured client would abort every `timeseries.get_range`
([#38]) whose download outlasts it — mid-stream, as a `TaskCanceledException` that reads like a
cancellation rather than a timeout. The linked-token form is both the modern .NET recommendation
and the only one that does not name a banned BCL type. **Nothing asserts this and nothing can:**
comparing two `TimeSpan`s calls `TimeSpan.op_Equality`, which `BannedSymbols.txt` forbids, so there
is no reachable member for a test to read. The assignment itself is clean — RS0030 flags the banned
type's own members and operators, not a value that merely has that type.

The zstd-framed JSONL reader is built here and **no historical endpoint calls it**. The issue's
scope calls the two readers "the two response readers every endpoint uses"; that is wrong, and the
correction is to the claim rather than to the scope. `handle_zstd_jsonl_response` has **zero call
sites in `databento-rs/src/historical/`** — all four are in `src/reference/` (`security.rs:49` and
`:76`, `corporate.rs:58`, `adjustment.rs:50`), which is M4, and none of [#36]–[#39] will call it.
It is built here anyway for two reasons rather than one: upstream defines it in
`historical/client.rs:212-229` even though only `reference/` calls it, so this is the faithful
placement and a reader comparing the two files finds it where they expect; and [#34]'s harness
already serves exactly that shape through `MockHistoricalResponse.ZstdJsonLines`, so it could be
tested against an oracle written before it existed. The frame sits in the HTTP body rather than
being announced in `Content-Encoding`, which is why the client decompresses it itself — there is no
`Content-Encoding` for `HttpClient` to act on — through the linked `Internal/ZstdDecompressor.cs`,
as CLAUDE.md requires.

The independent-oracle rule is **restated as what it always meant, and enforced rather than
stated**. Before this issue, `tests/DatabentoDotNet.Historical.Tests` had no `ProjectReference` to
`DatabentoDotNet.Dbn` and its csproj called that absence "the rule this project must not break" —
the [#34] ruling above. This issue gave `DatabentoDotNet.Historical` its own reference to the codec,
because `ApiKey` and `UserAgent` have lived there since [#32] and `VersionUpgradePolicy` since M1,
so the test project acquired it transitively and the build stopped enforcing anything by omission. The
rule's actual content is narrower than "cannot see the codec": the harness must not manufacture the
bytes it serves with the codec those bytes exist to check, which is why `SyntheticDbnFragment`
hand-builds DBN. It is now enforced with the mechanism this repo already uses for the NodaTime
rule — a project-scoped `BannedSymbols.txt` in that directory banning
`T:DatabentoDotNet.Dbn.MetadataEncoder`, the one type that could produce an oracle's bytes.
`BannedApiAnalyzers` reads every `AdditionalFiles` entry of that name, so the root file keeps
applying and this one adds to it. `Symbols` is deliberately *not* banned: building one never
touches the bytes the harness serves, and the composition test below needs it. The rejected
alternative was keeping the no-reference rule as written, which after this issue could only have
been bought by refusing the composition test the issue owes. Nothing else would buy it: an
`InternalsVisibleTo` would not, because `DatabentoDotNet.Historical` must reference
`DatabentoDotNet.Dbn` for `ApiKey` and `UserAgent` regardless, so the transitive path this test
project acquires exists whatever the codec chooses to expose.

That composition test is the last thing this issue settles, and it is worth naming what it settles.
`tests/DatabentoDotNet.Historical.Tests/HistoricalClientCompositionTests.cs` holds four tests, and
the first is the one [#35]'s own issue comments asked for by name:
`Post_ComposesARealSymbolsAndARealDateTimeRangeIntoUpstreamsExactForm`. **Nothing before this issue
compiled `Symbols` and `DateTimeRange` into the same assembly** — [#32], [#33] and [#34] were built
in parallel and each passed its own review, but `src/DatabentoDotNet.Historical` had no reference to
`DatabentoDotNet.Dbn` until this issue added one. The join was expected to need no `using`, since a
file in `namespace DatabentoDotNet.Historical.Tests` resolves `Symbols` by walking outward to the
enclosing `DatabentoDotNet` namespace — but that was reasoning, and the file compiling is the proof.
Three further facts [#36]–[#39] may rely on are each pinned to a named test rather than to an
implementer's report, because a claim anchored to a test survives a refactor and a claim in a report
does not: repeated `X-Warning` headers arrive as separate values and are parsed one array at a time
(`Warnings_AreLoggedFromEveryOccurrenceOfTheHeader`); a request-level `Accept` *replaces* the
client's default for that request and leaves the next one alone
(`Accept_OverridesTheDefaultForOneRequestOnly`); and a `BaseUrl` carrying a path keeps it when
`v0/{slug}` resolves against it, query or no query
(`BaseUrlCarryingAPath_KeepsThatPathWhenTheSlugIsResolvedAgainstIt`,
`BaseUrlCarryingAQuery_StillKeepsItsPath`). One thing that file is *not*:
`ApiKey_AppearsInNoSurface_WhenQueryAndFormValuesComeFromRealSymbolsAndDateRanges` is not a broader
credential check than `ApiKey_TravelsInTheAuthorizationHeaderAndNowhereElse` in
`HistoricalClientTests.cs`. It scans the same three surfaces — `RawQuery`, `Body`, `Headers` — over
the same harness guard; what it adds is a different *input path*, values produced by a real
`Symbols.ToApiString()`, `DateRange.StartIsoDate` and `DateTimeRange.StartUnixNanoseconds` rather
than by fixed string literals. That distinction is the whole of its value and is not to be
flattened back out.

**One parameter type prices the request it sends, and money is `decimal` ([#36]).**
`MetadataQueryParams` is one type across `get_record_count`, `get_billable_size`, `get_cost`
and — from [#38] — `timeseries.get_range`; upstream declares it once as `GetQueryParams` and
aliases it three times (`metadata.rs:348-359`). The rejected alternative is a params type per
endpoint, matching upstream's three aliases more literally, and it loses because the entire value
of `get_cost` is that it prices *the request you are about to send*: a caller who prices with one
type and then hand-assembles a second for `get_range` has been failed by exactly the mistake a
shared type rules out, and separate types make sending that different request the easy path
instead of the hard one. Money is `decimal`, both in `get_cost` and in `list_unit_prices`. The
rejected alternative there is `double`, matching upstream's `f64` exactly, and it loses because
upstream's `f64` is a Rust std limitation rather than a choice — Rust's standard library has no
decimal type — while a unit price here is multiplied by a record count before a caller ever sees a
figure, which is precisely where binary floating point's rounding compounds into a wrong dollar
amount. [#36]'s own definition of done named only `get_cost`; extending the same rule to
`list_unit_prices` was a deliberate call rather than an oversight, because both endpoints report
the same dollars-and-cents quantity out of the same client.

[#7]: https://github.com/jerbersoft/databentodotnet/issues/7
[#10]: https://github.com/jerbersoft/databentodotnet/issues/10
[#32]: https://github.com/jerbersoft/databentodotnet/issues/32
[#33]: https://github.com/jerbersoft/databentodotnet/issues/33
[#34]: https://github.com/jerbersoft/databentodotnet/issues/34
[#35]: https://github.com/jerbersoft/databentodotnet/issues/35
[#36]: https://github.com/jerbersoft/databentodotnet/issues/36
[#37]: https://github.com/jerbersoft/databentodotnet/issues/37
[#38]: https://github.com/jerbersoft/databentodotnet/issues/38
[#39]: https://github.com/jerbersoft/databentodotnet/issues/39
[#40]: https://github.com/jerbersoft/databentodotnet/issues/40
[#44]: https://github.com/jerbersoft/databentodotnet/issues/44
[#45]: https://github.com/jerbersoft/databentodotnet/issues/45
[#42]: https://github.com/jerbersoft/databentodotnet/issues/42

---

## 6. Milestone 4 — `DatabentoDotNet.Reference` (1.0 blocker)

> Tracked by [#8](https://github.com/jerbersoft/databentodotnet/issues/8) · milestone `M4: Reference data`

**DECIDED: 1.0 is full parity with `databento-rs`** — Live + Historical + reference data.

A separate client (`reference.rs` upstream), same `hist.databento.com` host, API version and Basic
auth, but **responses are zstd-compressed JSONL, not DBN** — this needs its own response handler,
not the M1 record decoder. Requests are POSTs with form-encoded bodies.

### The split

[#8] is decomposed the way [#7] was, into ten issues whose order is the dependency order rather than
a preference:

| Issue | What it delivers | Depends on |
|---|---|---|
| [#48] | `DatabentoDotNet.Reference` project and `ReferenceClient` | — |
| [#49] | The reference range, whose end is optional | — |
| [#50] | The twelve closed enums and their wire codes | [#48] |
| [#51] | The seven open enums that carry a code they do not know | [#48] |
| [#52] | Streaming zstd-JSONL, and the sort a stream cannot do | [#48] |
| [#53] | `adjustment_factors.get_range`, and `double` versus `decimal` | [#49]–[#52] |
| [#54] | `security_master.get_range` and `get_last` | [#53] |
| [#55] | `corporate_actions.get_range` and its three open maps | [#53] |
| [#56] | `corporate_actions.list_events` and `list_enums` | [#48], [#51] |
| [#57] | Opt-in tests against the real reference API | [#53]–[#56] |

**M3 already paid for most of what M2 had to build from nothing, which is why there is no
[#34]-shaped harness issue here and no [#35]-shaped transport issue.**
`HistoricalClient.SendZstdJsonLinesAsync` was written in [#35] *for these endpoints* and has no M3
caller; `SendAsync` already POSTs form-encoded bodies to `v0/{slug}` under Basic auth;
`MockHistoricalGateway` already serves zstd-framed JSONL chunked, and `StubHistoricalClient` is the
independent oracle for it. What is left is genuinely new: the types, the enums, the streaming
reader, and the six endpoints.

[#53] goes first among the endpoints, and not because it is alphabetical. It is the smallest model —
28 fields against 50 and 104 — and it carries `factor`, the multiplier applied to historical prices.
The decision about how a rate is represented gets made once, where it bites hardest, on the smallest
review surface; [#54] and [#55] then follow it instead of each deciding again.

[#56] is separable from [#55] despite sharing a subclient: a different HTTP method, plain JSON
rather than a zstd frame, a different type family, and no range parameters at all. It is also the
only pair in M4 that is near-certainly free to call, which makes it the cheapest place to meet the
real API — the same argument that pulled [#44] out of [#40].

Endpoints, grouped as upstream does:

- **`security_master.*`** — `get_range`, `get_last`. *([#54].)*
- **`corporate_actions.*`** — `get_range` *([#55])*; `list_events`, `list_enums` *([#56])*.
- **`adjustment_factors.get_range`**. *([#53].)*

`list_events` and `list_enums` return schema-describing maps — useful for validating the
strongly-typed models we generate for corporate actions, and [#57] is where that validation is
pointed at the live endpoint rather than at our own fixtures.

### Four decisions the split surfaced

Each is owned by the issue that has to make it; recorded here because each one is invisible in six
months and each one is a departure from either upstream or from the obvious .NET answer.

**1. How `DatabentoDotNet.Reference` reaches the transport ([#48]).** Upstream has one crate and so
has no version of this problem: `ReferenceClient` reuses `historical::{handle_response,
handle_zstd_jsonl_response, AddToForm, HistoricalGateway, API_VERSION}` through crate-internal
visibility. Separate .NET assemblies have no equivalent, and this repo declares no
`InternalsVisibleTo` anywhere — deliberately. The recommendation on that issue is a
`ProjectReference` on `DatabentoDotNet.Historical`, whose transport is already `public` on purpose
and already carries the JSONL reader; the two clients share a host, an API version, a gateway type
and an auth scheme, so the dependency is honest rather than incidental.

**2. `Unknown(String)` has no C# enum ([#51]).** Seven of the nineteen reference enums end in a
variant that carries the unrecognised code as a payload, so an ISO code Databento adds next month
round-trips through upstream untouched. A C# `enum` cannot hold a payload. An `Unknown = -1` member
compiles and **loses the string** — a caller handed `Country.Unknown` cannot tell Kosovo from a typo
and cannot echo the value back into a filter, which is strictly worse than upstream on the axis
upstream chose. The recommendation is a wire-string value type with static well-known members, so
`Country.Us` still reads like an enum at a call site. The other twelve enums are closed sets and
stay plain enums that throw on an unknown code ([#50]) — that difference is the whole reason the two
are separate issues. (**[#58]'s probe moved three of those twelve to [#51]**; the paragraph stands as
written because it is what the split believed, and the entry below is what the API said.)

**3. Upstream buffers and sorts; this milestone's definition of done requires streaming ([#52]).**
All four call sites read the whole response into a `Vec<T>` and then sort it — by the `index`
parameter, or unconditionally by `ts_effective` or `ex_date` — and `handle_zstd_jsonl_response`
returns a `Vec` precisely so they can. A stream cannot be sorted. The recommendation is that the
streaming reader preserves server order and does not sort, **after asking the server whether its
order is already that order** — a question the mock cannot answer, because it returns the lines it
was handed. [#57] owns the probe and carries the answer back.

**4. Whether a rate is a `double` or a `decimal` ([#53]).** Upstream uses `f64` for all twelve
numeric fields across the three models, because that is what `serde_json` hands it. Here the choice
is open: the wire carries decimal text, `decimal` round-trips it exactly and `double` does not, and
`factor` multiplies prices. A 1-for-3 split ratio is `0.3333333333333333` as a `double` and the
declared text as a `decimal` — the same argument CLAUDE.md makes for `Instant` over a 100 ns
`DateTime` tick, restated for money. It has a real cost, which is why it is a decision and not an
assumption: `decimal` has the narrower exponent range, so a value the API can express and `decimal`
cannot would throw where upstream approximates. Probe the magnitudes before committing. (**The
round-trip half of that argument is false and [#53] measured it so**; the paragraph stands as
written because it is what the split believed, and the entry below is what .NET actually does. The
conclusion did not change — the reason did.)

**Definition of done:** all six endpoints covered, plus the two things that only exist at the seam
between the sub-issues, each stated as the measurement that settles it rather than as an intention:

- **A response far larger than any buffer streams with flat memory** ([#52]), measured with
  `GC.GetAllocatedBytesForCurrentThread()` in the style of `TimeseriesAllocationTests`. Per-row cost
  that does not grow with the row count is the property that makes a full security master workable,
  and unlike a full security master it runs in CI in seconds. Per-row allocation cannot be *zero*
  here the way it is on the DBN path — every row is a JSON object deserialized into a class — so the
  property asserted is that it is flat, not that it is absent.
- **The enums we ship agree with the enums the server reports** ([#57]), checked against a live
  `corporate_actions.list_enums` rather than against our own fixtures: every group has a type, every
  code is either a known member or lands in the `Unknown` carrier without throwing, and codes the
  server has that we do not are named in the failure message. A test that merely passes would hide
  exactly the finding this is for.

Four things a naive port drops, each carried by the sub-issue that owns it:

- `start` and `end` are Unix **nanoseconds**, and `end` is **omitted entirely** when the range is
  open rather than sent empty — [#49].
- `compression=zstd` is hard-coded on every `get_range` and is not caller-settable: the response
  handler requires the frame — [#53]–[#55].
- `allocate_isins` defaults to `true` and can create new ISIN allocations on an ISIN-limited plan.
  That is a billing consequence hiding in a default — [#54], gated in [#57].
- Reference data is a **separate Databento product**, so a 403 on an account entitled for historical
  is a legitimate outcome and has to read as one rather than as a mysterious failure — [#57].

### Decisions made during implementation

The split above recorded four decisions as *questions the sub-issues would have to answer*, written
before any of [#48]–[#57] had a line of code. This is where the answers land as they arrive; it
grows one entry at a time and takes a count in its title when M4 is done, the way §5's did.

**The endpoint that describes the data shipped before the endpoint that carries it ([#56] before
[#55]).** The two are independent — [#56] depends on [#48] and [#51], not on [#55] — so the order
was free, and taking the smaller one first buys two things. It creates `CorporateActionsClient`,
which [#55] then adds one method to rather than both of them racing to declare it. And it ships
`EventDocField`, whose `group` is the server's own statement of which of `CorporateAction`'s three
open maps every field lands in — so [#55] writes those maps against a documented vocabulary instead
of inferring one and reconciling later.

**The one M4 endpoint pair with an oracle that is not our own reading ([#56]).** `Data/` holds the
live API's responses to exactly these two endpoints, captured verbatim by [#58], so the mock serves
production bytes and what the client makes of them is checked against `ReferenceEnumFixture` — which
reads the same bytes with `JsonDocument` and none of this library's models. Everywhere else in M4
the harness and the client were written from one reading and can agree with each other about a
misreading, which is the argument [#57] exists to settle. It still owes the other five endpoints
that; it owes these two less.

**`participation` is not `MandVolu`, and the endpoint said so ([#56]).** `EventDoc.participation` is
an `Option<String>` upstream while `MandVolu` models what reads like the same concept, and folding
one into the other is [#45] in miniature: two vocabularies that agree in meaning and disagree on the
wire. They disagree. `list_enums`' `MANDVOLU` group reports `M`, `V` and `W`; the field reports
`mandatory`, `voluntary` and `mandatory_voluntary`. Not one code is shared, so the closed enum would
have rejected every value the endpoint sends. Both sides of that come from the captured responses
and are asserted rather than recorded in prose — the [#45] lesson applied before the mistake instead
of after it.

**`get_last` cannot inherit a range, because the compiler will not let it ([#54]).**
`security_master.get_range` and `security_master.get_last` take the same four parameters plus, for
the first, `index`, `start` and an optional `end`. C# can express that as inheritance — derive the
range parameters from the last parameters and the four shared properties are written once — and
that is exactly the arrangement this issue avoids. It would let a caller hand a fully specified
range to `GetLastAsync`, where the range is *silently dropped* rather than refused. Two sealed
records with no relationship between them make it a compile error instead, and the four properties
written twice are the price.

**The duplication is treated as a claim to check rather than as an invariant to trust.** Both
endpoints are driven at `MockHistoricalGateway` with every shared parameter set identically, and
the difference between the two recorded form bodies is asserted **as a set**: `{end, index, start}`
in one direction, empty in the other, with equal values on every key they share. A presence check
on `index` would pass on a `get_last` that had quietly kept `start`.

**The index is sent and is not sorted by, and that is one decision rather than two.** Upstream uses
the same value twice — the server filters on it (`security.rs:36`) and the buffered `Vec` is then
sorted by whichever field it names (`:50-53`). Streaming drops the second use and keeps the first,
so `index` is on the wire in every `get_range` request. `get_last`'s sort is the easier half: it is
unconditional, sends no `index`, and is purely a rearrangement of a buffer upstream had already paid
for — dropping it is [#52] restated, not a second call. Both are asserted as measurements: three
rows served out of order arrive out of order, and the request that asked for `ts_record` is shown to
have said so.

Three smaller answers this issue also settled. **`SecurityMasterIndex` is the one enum in this
package whose `default` is a member** rather than an undefined byte — the nine closed enums are
byte-backed so a response field this library failed to populate cannot pass for a real code, and
this one is only ever written to the wire, where upstream's `#[default]` is `TsEffective`.
**`voting` is the only place a closed enum meets an `Option`**, so it is a `Voting?` — and it needs
no second converter, which was checked rather than assumed: `System.Text.Json` answers the `null`
token for the `Nullable<T>` itself, and only the empty *string* would reach the converter, which the
`VOTING` dictionary group says does not occur. And **three optionality disagreements with
`AdjustmentFactor` are reproduced rather than reconciled** — `operating_mic`, `exchange` and
`security_type` are each optional on one model and required on the other, upstream's own typing in
both cases; [#57] can say which library is right about each.

**A rate is a `decimal`, and the argument for it is not the one the split wrote down ([#53]).**
The conclusion the split predicted is the one that shipped: all four rate fields on
`AdjustmentFactor` are `decimal` — `Factor` and `Sentiment` plain, `Close` and `GrossDividend`
nullable, each following upstream's own `Option`. [#54]'s `par_value` and `vote_per_sec` followed
it, and so did [#55]'s five — including the values of the `rate_info` map, because a map of rates is
still rates.
**The reasoning had to be replaced.**
The split's case was that "the wire carries decimal text, `decimal` round-trips it exactly and
`double` does not", with a 1-for-3 split ratio as the example. Measured on .NET 10, that is wrong:
`System.Text.Json` writes a `double` in **shortest-round-trip** form, so any wire value of
seventeen significant digits or fewer comes back spelled exactly as it arrived. `0.3333333333333333`
round-trips through both. So does upstream's own fixture value `0.995833170541121`. The example
chosen to make the case does not make it.

What `double` actually loses is the **value**, not the text. `0.995833170541121 * 51.19` is
`50.97669999999998399` exactly and `50.97669999999998` in binary floating point. A factor exists to
be multiplied by a price, so the product is the number that matters — and that is the argument that
survives, restating for reference data the call `MetadataClient.GetCostAsync` and `BatchJob.CostUsd`
already made for money on the historical side.

**The cost is two-sided, and only one side is loud.** Above `decimal.MaxValue` (~7.9e28)
`System.Text.Json` throws a `JsonException` naming the property path: diagnosable, and confined to
the row rather than the stream. Below ~1e-28 it does **not** throw — the value silently reads as
zero, which is the worse failure and the one a "decimal has a narrower range, so it would throw"
framing hides. Both are asserted as tests, so a framework change that made either quieter breaks a
build. Neither bound is reachable by a price, a dividend, a ratio near one, or a split factor.

**The magnitudes actually present in a live response are unprobed, and that is a disclosure rather
than an oversight.** [#53] asked for a probe before committing and named the fallback: say so and
ship `decimal?` with the risk written down. `adjustment_factors.get_range` bills, so asking is not
free and no spend was authorised for it; **[#57]** owns the gated request that can. What replaced
the probe is not preference — it is the mechanism measured locally, which is the half that was
actually in doubt.

*Two smaller answers fell out of the same issue.* **`reason` and `option` stay bare `uint`s**, and
that was checked rather than assumed: the vendored `corporate_actions.list_enums` response has 235
groups and describes neither field in any of them — its `REASON` group is a different vocabulary
(`C`, `H`, blank), and the only four groups whose codes are numeric are `CLASSCODE`, `INDUS`,
`MKTSG` and `REPAYSRC`. That is consistent with `AdjustmentStatus`: the dictionary documents
*corporate actions*, and these are `adjustment_factors` fields. And **upstream's `currency` /
`dividend_currency` asymmetry is reproduced** — a plain `String` beside a `Currency`, in adjacent
lines — because tidying it would be a behavioural change to a field neither library has probed.

**The streaming reader does not sort, and says so in those terms ([#52]).** Upstream's
`handle_zstd_jsonl_response` (`historical/client.rs:212-229`) returns a `Vec<R>` precisely so that
its callers can sort it, and all four of them do: `reference/security.rs:50-53` by `index` and `:77`
by `ts_effective`, `corporate.rs:59-63` by `index`, `adjustment.rs:51` by `ex_date`. **A stream
cannot be sorted — sorting is what buffering *is*** — so `ReadZstdJsonLinesStreamAsync` and
`SendZstdJsonLinesStreamAsync` yield rows **in the order they arrived** and claim nothing more than
that. The documentation says "in the order they arrived" and deliberately never says "sorted", "in
order", or "as upstream returns them".

Whether that is a difference anyone can observe turns on a prior question — *is the server's order
already the sorted order?* — which **[#57] owns**, because it cannot be answered here: the mock
returns the lines it was handed, so it agrees with whatever we assumed, and only the live API
settles it. The streaming reader ships without waiting for the answer because none of its five
acceptance criteria depend on it.

Two consequences. **The buffering `ReadZstdJsonLinesAsync` stays**, as the non-streaming path for a
caller who wants the whole list — to sort it, to count it, to index into it — and both halves of
each pair now name the other in their remarks. And **there is no sorting overload**: [#53]–[#55]
each decide for themselves whether their endpoint sorts, over the buffering reader, rather than
inheriting a decision from the transport.

**The issue was wrong about `date_info`'s timestamp format, and it said to check ([#55]).** #55
predicted that `date_info`'s custom deserializer "means the same timestamp format as the fixed
columns and not ISO-8601 by default", and instructed the implementer to "check it rather than
assuming". Checking overturned it, in the other direction. The two fixed timestamps go through
`deserialize_date_time`, which tries ISO 8601 and **falls back** to a legacy space-separated
`YYYY-MM-DD HH:MM:SS[.ffffff][+HH:MM]`; `date_info` goes through
`deserialize_opt_date_time_hash_map`, which is **ISO 8601 only, with no fallback**
(`databento-rs/src/deserialize.rs:7-53`). The map is the *stricter* of the two, not the looser.

The port does not reproduce the asymmetry, and the reason is that it cannot cost anything. The two
formats are mutually unambiguous — one separates date from time with a `T`, the other with a space —
so accepting both cannot change how any value *reads*, only whether a row is *rejected*. One
`InstantJsonConverter` therefore serves the fixed columns and the map alike, and a test pins the
consequence so it stays a decision rather than an accident. This is the second time an M4 issue's
own stated premise turned out to be the thing worth testing; the first was [#45].

**A missing map is an error, not an empty map ([#55]).** The same issue asked which of the two
upstream produces for an absent key, "with a test that distinguishes them". Neither: `corporate.rs`
carries **no `#[serde(default)]` at all** — three `serde` attributes in the file, all
`deserialize_with` — so `date_info`, `rate_info` and `event_info` are required fields and a row
omitting one fails to deserialize. `required` reproduces that. An *empty* map is an ordinary answer
and the commonest one. And a third state the issue did not name is real and independently
observable: **a key present with a `null` value is a value, not an absence**, which is what
upstream's `Option` in `HashMap<String, Option<T>>` expresses and what `Instant?`/`decimal?`/`string?`
express here. Upstream's own fixture row makes that point on `rate_info`, whose two keys both carry
`null`, so it is asserted against Databento's row rather than only against one this repository wrote.

**Upstream's own "unknown event" fixture is not unknown to this library ([#55]).** `corporate.rs`'s
second test feeds `related_event: "CORR"` precisely because upstream's hand-written `Event` enum has
no such variant, and asserts it round-trips as `Event::Unknown("CORR")`. Here it round-trips as a
*recognised* code: the table is generated from the server's `EVENT` dictionary group, 141 codes to
upstream's 60, and "Correction" is one of the 81 upstream lacks. The row still reads back as `CORR`,
which is the assertion that matters — but the open-carrier behaviour itself needed a code neither
library knows, and is tested with one. This is [#50] and [#51]'s argument arriving as evidence
rather than as prose.

**Nine closed enums, byte-backed, and an unrecognised code throws ([#50]).** The other half of the
line [#51] drew. The nine char-coded reference enums are plain C# enums — 42 variants — and each
keeps upstream's `#[repr(u8)]` backing as `enum T : byte` with `Cancelled = (byte)'C'`. The issue
left that call to the implementer on the grounds that nothing here is binary; it is kept anyway,
and the third reason is the one the issue could not have weighed. It is upstream's own
representation and the porting rules make the Rust source authoritative for wire format. The codec
already models char-coded enums this way, so a reader who knows `DatabentoDotNet.Dbn.Action`
already knows these. And it makes `default(T)` an **undefined** value: byte 0 is in none of the
nine alphabets, so a field a response never set reads as something `Enum.IsDefined` rejects, where
a plain 0-based enum would have read a missing `Fraction` as `Fraction.Cash`. That last one is
asserted for all nine, and again end-to-end against a `{}` body.

*What differs from the codec's char enums is the wire, not the type.* On the DBN wire a `Side`
**is** the raw ASCII byte and has no text form at all; here the byte never appears, because the
reference API is JSON and carries a one-character *string*. So these do have a text form, it is
exactly one character long, and a string of any other length is as unrecognised as an unknown
letter — which is a failure mode the codec's enums cannot have.

*An unrecognised code throws, and the probe is what makes that safe.* Upstream returns an error for
an unknown char (`enums.rs:44-55` and its eight siblings), and this library keeps that rather than
carrying the code the way the ten open types do. The justification is the `list_enums` probe
recorded above: eight of the nine alphabets are exactly current against the live server, so a code
outside one really does mean this library's table is stale. The message names the offending code,
and the fixture check names any code the server has that we do not — both halves verified by
breaking the table on purpose and reading the failure.

*A blank is a value for exactly two of the nine, and the fixture said so rather than the issue.*
`FRACCD`, `FRACTIONS` and `PAYTYPE` each list a null-code entry described as "A Blank value is
possible"; the other six groups do not. `Fraction` and `PaymentType` are also the two upstream
declares as `Option<T>` (`corporate.rs:373`, `:358`), so those two ship a second converter —
`JsonConverter<T?>` with `HandleNull => true` — named on the property rather than on the type,
since a type carries only one `[JsonConverter]`. `Voting` is `Option<Voting>` upstream too and does
**not** get one: a serde `Option` also covers an absent or null field, which `System.Text.Json`
answers for a `Nullable<T>` on its own. The empty *string* is the only case that needs a converter,
and the dictionary is the authority on which fields can send one.

**Ten open code types, one shape, and the analyzer objections it drew ([#51]).** The recommendation
held: a `readonly record struct` over the wire string with the known values as static members, so
`Country.Us` still reads like an enum and a code Databento adds next month is carried rather than
lost. Three things the split could not have known.

*A public `IReferenceCode<TSelf>` interface with static abstract members collapses what would have
been ten copies.* One `ReferenceCodeJsonConverter<T>` serves all ten types, closed over each by the
`[JsonConverter]` attribute the type carries — which is what the `System.Text.Json` source generator
reads, so they are AOT-safe wherever a generated context later holds one. One
`ReferenceCodeFilter.Render<T>` serves the three list filters upstream writes three times
(`reference.rs:252-297`). The interface is public rather than internal because a public converter
cannot be constrained by an internal type, and it earns the place: it is the contract that says what
these ten types are.

*`default` is the absence of a value, and that is not a technicality.* A blank is a real thing the
dictionary carries — `SECTYPE`, `FREQ` and `EVENTSUBTYPE` each have a null-code entry, and 148 of
the 235 groups do — so `From(null)` and `From("")` both give `default`, whose `Code` is null. The
constructor refuses a blank, so that state is only ever reached deliberately. The JSON converter
sets `HandleNull` because `System.Text.Json` will not otherwise hand a null token to a
non-nullable struct's converter at all; it throws instead.

*The member names are the wire codes, and two of them made the analyzers object.* Nine types name a
member as the PascalCase of its code, which matches upstream **exactly** — all 246 countries, all
179 currencies, all 67 sub-types. `Frequency` is the exception in both libraries: upstream names it
after the description when that description is a single word and falls back to the code when it is
not, which is why `Intonmat` and `Itm` — sharing the description "Interest on Maturity" — keep
theirs. The two codes upstream lacks, `BIW` and `FRT`, fall out of that same rule as `BiWeekly` and
`Fortnightly` rather than being invented. Against that, `CA1716` objects to the type name `Event`
(reserved in Visual Basic) and `CA1720` to the members named `Int` (whose code is `INT`,
"Interest"). Both are settled in favour of the dictionary, because these are not identifiers this
library chose. [#51] settled them with a `[SuppressMessage]` and a scoped `.editorconfig` entry;
**[#59] deleted both**, because marking the ten files `<auto-generated>` exempts them from analyzers
outright. The reasoning did not go with them — it moved into `Event`'s own remarks, where it is read,
rather than sitting in a suppression that suppresses nothing.

*The 730 members had no checked-in generator, and now do ([#59]).* They were produced by a
throwaway script, which left the next fixture re-capture with no mechanical path back. That was a
gap rather than a decision: `tools/generate-publishers.py` had solved the identical problem for the
268 `dbn` publisher variants, and the answer was to follow it. `tools/generate-reference-codes.py`
regenerates all 730 byte-identically and writes **nothing at all** when the fixtures are not the
shape it knows — both properties asserted rather than claimed. A Roslyn source generator was
rejected for two reasons: it would make `ReferenceCodeTableTests` compare the file to itself, and it
would let a re-capture change or *remove* public API with nobody reading the diff.

*The definition of done named `ZZ` as a country code that does not exist, and it does.* It means
"Unclassified", it is in the 246, and upstream models it as `Country::Zz`. The test uses a genuinely
absent code and **asserts its absence from the fixture** rather than assuming it, so a re-capture
that adds the code fails loudly instead of quietly making the test vacuous. The table tests read the
shipped members by reflection for the same reason: a hand-written list of 730 expected codes would
be a second copy of the table, agreeing with the first because it was typed from it.
**The reference range is a Reference type, not a Historical one ([#49]).** [#49]'s own text
proposed putting it beside `DateTimeRange` on the grounds that [#48]'s project reference makes the
placement free. It is not free, and the `.csproj` [#48] shipped had already said so — it named the
condition under which the package would declare NodaTime as "the reference range (#49) is the first
thing that will". No historical endpoint accepts an open-ended range, so putting `ReferenceDateTimeRange`
there would add public surface that nothing in that package consumes, standing next to the type a
caller should reach for instead. Upstream draws the same line: `Start` and `End` are declared in
`reference.rs`, not shared down from `historical` the way `AddToForm` and `handle_zstd_jsonl_response`
are. The dependency direction settles it — the `DateTimeRange` → `ReferenceDateTimeRange` conversion
is possible from here and the reverse would not be possible from there.

*The optional end reintroduces a problem `DateRange` and `DateTimeRange` solve for free.* Both of
those detect a `default` value by their own invariant — an end not strictly after the start is
exactly the state `default` leaves them in. Here an absent end is **legal**, so
`default(ReferenceDateTimeRange)` carries the same field values as `StartingAt(NodaConstants.UnixEpoch)`
and would render as a well-formed request for everything recorded since 1970, against endpoints that
bill by what they return. The type therefore carries a private construction flag, which is the one
thing in it with no counterpart in the Rust: `Option<OffsetDateTime>` has no default-constructible
struct to guard.

*The `end`-versus-no-`end` branch lives on the range, not in the three parameter sets that will
carry one* ([#53]–[#55]) — one place to get it right rather than three, which is also where upstream
puts it. That an open range sends the key set `{start}` and not `{start, end}` with an empty value
is asserted on `MockHistoricalGateway`'s **recorded form** over a real socket rather than on the
rendered list, because a list of pairs is one `FormUrlEncodedContent` away from the wire and a unit
assertion on it would not notice an empty `end=`. Deliberately breaking the renderer to emit one
fails that test, which is how the assertion was confirmed to be load-bearing rather than decorative.

*What the type does **not** claim is that the end is exclusive as a fact.* Upstream's three doc
comments say so and nothing in either library has asked the server, which is the exact shape of the
assumption [#45] found to be false for `get_dataset_condition`. It is documented as *documented
exclusive and unprobed*, the way `DateTimeRange` already is for the two historical endpoints that
cost money. [#57] owns the probe.

**The enum tables come from the API, not from `enums.rs` ([#58], reshaping [#50] and [#51]).**
`corporate_actions.list_enums` and `list_events` were probed against the live API before either
issue had a line of code — both free discovery endpoints, both `200 OK`. That answers the entitlement
question only for *discovery*: reference data is a separate Databento product, the billable
`get_range` endpoints were not called, and whether a 403 waits there is still [#57]'s to find out. Upstream turns out to be behind the server on
three of the ten enums this library will type.

*`SecurityType` models 30 codes and `SECTYPE` reports 64.* Of the 235 groups the endpoint returns,
`SECTYPE` is the only one containing all 30, so the mapping is determined rather than guessed. The
sharp edge is `adjustment.rs:109` — `pub security_type: SecurityType`, **not** `Option` — so an
unmodelled code fails the whole row rather than one field. `Frequency` models 14 of 16, missing
`BIW` and `FRT`. `Event` is stale in *both* directions: upstream carries `DIVEB` and `LTCHG` that
`list_events` does not document, and lacks `DIVIF` and `MFCON` that it does, with all four present
in the 141-code `EVENT` group.

*Against that, eight of the nine char-coded enums are exactly current* — `ACTION`, `FRACCD`,
`GLOBSTATUS`, `LISTSOURCE`, `LISTSTAT`, `MANDVOLU`, `PAYTYPE` and `VOTING` all match, and the ninth
(`AdjustmentStatus`) is simply outside a corporate-actions dictionary's remit. **So the line between
[#50] and [#51] is wire alphabet versus data dictionary, not `#[repr(u8)]` versus `String`.** A
single-byte alphabet is closed because a new value in it is a wire-format change; a dictionary
entry is not. `SecurityType`, `Frequency` and `OutturnStyle` moved to the open carrier — the last of
them exact today, and moved anyway, because the rule is about where a vocabulary comes from rather
than how many values it currently holds.

*This is a behavioural departure from upstream, not a structural one,* and PORTING.md §2 records it
as one: upstream **rejects** a `SecurityType` outside its 30 where this library will accept it. It
goes one way only — we accept rows upstream drops, never the reverse — and the probe is the evidence
that those rows are real.

*The two responses are vendored verbatim* ([#58]) so the tables are checked against the server's own
dictionary rather than against counts typed into an issue, which is [#57]'s definition of done
turned into an input instead of a late failure. They are the first fixtures in this repository that
did **not** come from a Databento-authored repository — they came off the wire — and
`Data/README.md` says so, because "vendored" means something else everywhere else here.

**How the reference package reaches the transport ([#48]).** Answered exactly as the split
recommended — a `ProjectReference` on `DatabentoDotNet.Historical`, with `ReferenceClient` sending
through the public `HistoricalClient` — and the implementation added three things the split could
not have known.

*No new transport code was needed, which is the strongest evidence the recommendation was right.*
`SendAsync` already composes `v0/{slug}`, already chooses query-versus-form by HTTP method, and
already attaches the Basic credential; `SendZstdJsonLinesAsync` was built in [#35] for these
endpoints. `ReferenceClient` is configuration, ownership and a transport property — nothing that
touches the wire.

*`DatabentoDotNet.Reference` carries no zstd at all,* and that is the one place the split's phrasing
was slightly off: every reference `get_range` returns a zstd-framed body, but this package does not
need to *own* the decoding. The reader stays on `HistoricalClient`, which is where upstream puts it
too — `handle_zstd_jsonl_response` is defined in `historical/client.rs:212` even though only
`reference/` calls it. So CLAUDE.md's rule that every zstd call goes through one file stays true
with one *fewer* copy of `Internal/ZstdDecompressor.cs` in the repo, not one more.

*A second constructor takes an existing `HistoricalClient`,* so a consumer holding both packages
gets one connection pool to a host both APIs share rather than two. It has no upstream counterpart
and needs none: `reqwest::Client` is an `Arc`-wrapped pool, so two upstream clients in one process
are cheap in a way two `HttpClient`s are not. Ownership does not transfer — a borrowed transport
outlives the client that borrowed it. The five configuration properties then report *that*
transport's settings, and assigning one alongside that constructor **throws** rather than reporting
a credential no request carries.

*The test project reaches `MockHistoricalGateway` by referencing another test project* — the only
one of those in the repo. A `MockReferenceGateway` would be a second copy of ~1000 lines differing
only in which slugs it routes, with two places for a misreading of the wire protocol to be fixed in
one. The independent-oracle rule is untouched: that rule forbids a double that manufactures the
bytes it serves with the code those bytes exist to check, and this harness was written from
Databento's HTTP documentation before `ReferenceClient` existed. Sharing one harness across two
clients is the opposite of a harness agreeing with its client.

[#8]: https://github.com/jerbersoft/databentodotnet/issues/8
[#48]: https://github.com/jerbersoft/databentodotnet/issues/48
[#49]: https://github.com/jerbersoft/databentodotnet/issues/49
[#50]: https://github.com/jerbersoft/databentodotnet/issues/50
[#51]: https://github.com/jerbersoft/databentodotnet/issues/51
**The two documentation endpoints do not authenticate, which is what makes them free ([#57]).**
`RealReferenceApiTests` was written asserting that a syntactically valid but fake key is refused
with `401` — what `metadata.list_datasets` does with the same value. The first run said otherwise:
`corporate_actions.list_enums` and `corporate_actions.list_events` answer `200` with their complete
bodies, and answer identically to a request carrying no `Authorization` header at all. Measured
2026-08-29, credential-free, byte-for-byte identical to the vendored fixtures — same MD5s, 879,114
and 71,489 bytes. The control ran in the same minute and `corporate_actions.get_range` refused the
same fake key with `401 auth_authentication_failed`, so this is a property of these two endpoints
and not of the key, the host or the transport.

[#57]'s scope asked for the free classification to be *established rather than assumed*, warning
that "these are documentation `GET`s" is a prior and not a probe. The probe returned something
better than a price: **a request that carries no account cannot be billed to one.** That argument
holds for anyone's key rather than only for the account it was measured under, and it survives a
pricing change. It also makes `Data/README.md`'s re-capture a two-line `curl` that needs no
Databento account — worth having, because [#58] captured those fixtures precisely on the finding
that this dictionary *moves*.

**Reference data is three subscriptions, not one ([#57]).** The billable gate was opened and all
four endpoints answered `403 license_reference_dataset_no_subscription`, with
`payload.reference_dataset` naming which one refused — `"corporate actions"`, `"security master"`,
`"adjustment factors"` — and a distinct message for each. Nothing in this repository had modelled
that. `ReferenceClient` exposes three sub-clients because the *endpoints* group that way; it turns
out the *entitlements* do too, so an account can hold one and not the others and "does this key have
reference data" has no single answer. The failure message names the dataset for exactly that reason.

The refusal costs nothing — no rows are returned — which is the only reason this could be
established without an entitled key. It also means [#57]'s definition-of-done item about a 403
reading as a legitimate outcome rather than a mysterious failure is the one item the unentitled
account could *prove* rather than merely arrange for.

**What [#57] therefore could not answer, and why that is a state rather than a gap.** The three
questions it owes [#49], [#52] and [#53] — is the range end exclusive, is the server's row order
already the index's order, what magnitudes do the rate fields carry — each need a row, and no row
came back. All three experiments are written, gated on `DATABENTO_REFERENCE_REQUEST`, and run the
moment an entitled key exists; each names in its failure message exactly what the affected type's
documentation would have to become. `ReferenceDateTimeRange` still says *documented exclusive,
unprobed*, which is now a measured obstacle rather than an unopened question. The alternative —
quietly skipping on 403 — would have produced a green run that reported success for having asked
nothing.

**Fixture-versus-server is a better live check than ours-versus-server ([#57]).** [#50] and [#51]
transcribe the ten shipped tables from the vendored responses and assert against them offline, so
"our tables match the server" already has a baseline that fails the build. What that baseline cannot
notice is the fixture ageing. So the live test compares the *fixture* to the server and the offline
tests compare the *tables* to the fixture: between them every code is named on one side or the
other, and each test fails for exactly one reason rather than ten tests failing for the same one.
First run: no drift at all, in any of the 235 groups.

**The third `.env` parser was not written, and the reason the second one was is why ([#57]).**
`HistoricalCredentials` documented at length why it copied `LiveCredentials` rather than extracting
the shared sixty lines — extracting would have added a fourth test assembly to the solution and a
project reference between two deliberately independent harnesses. Neither cost exists at M4:
`DatabentoDotNet.Reference.Tests` already references `DatabentoDotNet.Historical.Tests`, for
`MockHistoricalGateway`. So `Resolve` and `IsEnabled` became public and `ReferenceCredentials`
carries only what is reference-specific. A copied rationale would have been the easy thing and the
wrong one; the argument for the second copy is the argument against the third.

[#52]: https://github.com/jerbersoft/databentodotnet/issues/52
[#53]: https://github.com/jerbersoft/databentodotnet/issues/53
[#54]: https://github.com/jerbersoft/databentodotnet/issues/54
[#55]: https://github.com/jerbersoft/databentodotnet/issues/55
[#56]: https://github.com/jerbersoft/databentodotnet/issues/56
[#57]: https://github.com/jerbersoft/databentodotnet/issues/57
[#58]: https://github.com/jerbersoft/databentodotnet/issues/58
[#59]: https://github.com/jerbersoft/databentodotnet/issues/59

---

## 7. Milestone 5 — Polish & release

> Tracked by [#9](https://github.com/jerbersoft/databentodotnet/issues/9) · milestone `M5: Polish and 1.0`

Decomposed into six sub-issues the way §5 and §6 were, and [#83] joined them once the first
real latency run showed what a two-clock figure cannot answer. The sequence below is the dependency order,
not a preference.

| Issue | Delivers | Depends on |
|---|---|---|
| [#63] | Public API surface locked via `PublicApiAnalyzers` | — |
| [#64] | Native AOT verified by publishing and *running* a binary | — |
| [#65] | Live end-to-end latency benchmark | — |
| [#83] | Gateway round-trip time, measured on one clock | [#65] |
| [#66] | Four runnable samples | [#63] |
| [#67] | DocFX site over the four packages — *built, then retired by [#70]* | [#63], [#66] |
| [#68] | NuGet publish, release automation, 0.x → 1.0.0 | all of the above |

**[#63] goes first, and it is the only ordering in this milestone that matters.** The surface is 210
public types and roughly 4,000 public member declarations, currently unlocked — nothing distinguishes
a deliberate addition from an accidental one. Samples and docs are both written against it, so
locking afterwards means editing them twice. It also runs the other way: a sample that needs an
awkward two-step to do an obvious thing is the clearest evidence a surface has a problem, and [#66]
landing *after* [#63] means that evidence arrives while the API can still change.

**[#68] goes last because it is the only irreversible step in the repository.** A published version
number is permanent. Everything else in M5 can be redone.

### What M0–M4 already paid for

Worth stating, because it is why this milestone is six issues rather than nine.

- **XML documentation on every public member is done, and has been since M0.**
  `GenerateDocumentationFile` plus `TreatWarningsAsErrors` means an undocumented public member has
  never compiled. Half of the old "XML docs on all public API; DocFX site" line needed no work at
  all, and [#67] is the site alone.

  **That framing was right and the conclusion drawn from it was wrong.** "The other half is the
  site" assumed a site was wanted. It was not: the doc comments already reach consumers through the
  `.xml` file `dotnet pack` ships, the wiki already held the prose, and [#70] retired the site
  without replacing it. Recorded here rather than edited away, because the mistake was in this
  paragraph before it was in any issue.

  **And then a site was wanted after all.** [#78] rebuilt it, [#80] published it, and [#82] made it
  the documentation outright — the wiki's ten guides moved into `docs/` and the wiki was retired.
  The paragraph above stays because it was an honest reading of the evidence in August. What it
  could not see is that "the wiki already held the prose" is an argument about *where the one copy
  lives*, not an argument against a site, and it stops applying the moment the wiki is the thing
  being replaced.
- **Decode throughput and allocated-bytes-per-record shipped in [#28]**, early rather than here.
- **The AOT and trim analyzers have been on since M0**, and have already shaped real decisions: the
  source-generated JSON contexts exist because the reflection overloads *fail the build*
  (IL2026/IL3050), and `ZstdSharp.Port` was chosen for being pure managed with no native asset and
  no per-RID build.

### Two decisions the decomposition made

**The generated reference tables go into the API baseline rather than being excluded ([#63]).** They
dominate it — `Country` is 248 public statics, `Currency` 181, `Event` 143, so 572 lines from three
generated files — and the tempting move is to exclude them for readability. That gives up the only
place a change would be seen. [#58] established that Databento's dictionary *moves*: that probe found
`SecurityType` at 30 of 64, `Frequency` at 14 of 16, and `Event` stale in both directions. A
regeneration that adds twelve countries genuinely is a public API change, and a diff naming them is
exactly the review artefact it deserves.

**An analyzer is not a verification ([#64]).** Three AOT and trim analyzers have been on for the
whole project and no AOT binary has ever been produced. What is established today is that nothing
*statically detectable* is wrong — which does not cover whether ILC accepts the assemblies, whether
the trimmer keeps what the code reaches, or whether the binary runs. Those fail at publish time or at
run time. So [#64]'s definition of done is two claims, not one: the publish succeeds with no
IL2xxx/IL3xxx warnings, **and** the resulting binary decodes the vendored corpus to the same record
counts the managed suite already asserts.

### Decisions made during implementation

**The whole baseline sits in `PublicAPI.Unshipped.txt`, and `Shipped` is empty ([#63]).** Normally
Shipped is a released version's surface and Unshipped is what has accumulated since. The dividing
line here is not *published* — `0.1.0-alpha` is on nuget.org and `0.9.0` ([#74]) will be — but
*promised*: `Shipped` should list a surface we have undertaken not to break, and under SemVer that
undertaking starts at 1.0.0. A beta exists to find out whether the surface is the right one, so
freezing it in a file called *Shipped* would assert the opposite of what the release is for. [#68]
moves all 3,801 entries across at 1.0.0, and that move is the release's own reviewable diff.

That placement would have been wrong if RS0017 policed only the Shipped file — removals would have
gone unreported, which is half of what the lock is for. It polices both. Changing an Unshipped
entry's value produces RS0016 for the real member *and* RS0017 for the stale one, verified by doing
it rather than by reading the documentation.

**`dotnet format` cannot generate the baseline, and the diagnostic can ([#63]).** The obvious route
is `dotnet format analyzers --diagnostics RS0016`, which reports success, rewrites nothing, and
leaves the files empty: `dotnet format` applies fixes to *source*, and RS0016's fix writes to an
`AdditionalFiles` entry. What does work is the diagnostic message itself — it carries the exact line
the file needs, `Symbol 'const DatabentoDotNet.Live.LiveGateway.DefaultPort = 13000 -> int' is not
part of the declared public API` — so the baseline is extracted from the compiler's own SARIF output
(`-p:ErrorLog=…`) rather than transcribed.

**Take the SARIF, not the build log.** The first attempt parsed MSBuild's file logger and produced
2,636 entries; the SARIF produced 3,801, and the difference is not a parsing bug. A parallel
solution build drops and coalesces diagnostics in the console and file loggers, silently — Dbn
reported 286 entries one way and 1,451 the other. A baseline missing a third of the surface would
have locked, built green, and quietly failed to police what it omitted. The check that caught it is
the only one that could: after writing the baseline, RS0016 must be **zero**, and it was not.

**Three things reading the baseline surfaced, which is why the issue required reading it ([#63]).**

*Nothing in an `Internal` namespace is public.* Asserted now rather than assumed.

*Every public field is `readonly`* — the `static readonly Duration` timeout defaults on `LiveClient`,
and the record-struct wire fields, which must be fields because a field's type **is** its wire layout.

*There is not one public setter in the entire surface.* 1,372 getters, 380 init-only, zero `set`.
That is a real immutability guarantee across four packages that nobody had measured, and from here
it is enforced: adding a setter fails the build until someone writes it into the baseline on purpose.

**ILC does its own trim analysis, and it does not see a `#pragma warning disable` ([#64]).** The
Roslyn analyzers report IL2026/IL3050 at compile time and a source-level suppression silences them.
ILC scans IL, has no idea a pragma was ever written, and reports the same violations again at publish
— as *errors*, since `TreatWarningsAsErrors` is repo-wide, so the publish fails. Verified by putting
a reflection-based `JsonSerializer.Deserialize` behind `#pragma warning disable IL2026, IL3050` and
watching `tools/aot-probe.sh` exit 1 with `ilc … exited with code -1`. That makes the AOT publish a
genuinely independent gate rather than a slower rerun of the analyzers.

**Nothing inside the process can tell a native binary from a JIT run of the same project ([#64]).**
The obvious in-process guard is `RuntimeFeature.IsDynamicCodeSupported`, and it is useless here:
`PublishAot` writes `"System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported": false`
into `runtimeconfig.json` for the ordinary `dotnet build` output too, so `dotnet run` on the probe
reports itself as having no dynamic code while running under the JIT. The claim is therefore made
from outside: `tools/aot-probe.sh` publishes, checks with `file(1)` that what came out is a native
executable rather than a managed assembly, and only then runs it. The program prints the flag as
evidence and asserts nothing from it.

**A failed ILC run leaves the previous binary where it was ([#64]).** Observed while testing the
above, not hypothesised: the publish failed, and running the path anyway printed a clean pass from
the binary published before it. `tools/aot-probe.sh` deletes the publish directory before publishing
for exactly that reason.

**The probe compiles the test projects' source; it does not reference them ([#64]).** Six files by
`<Compile Include=… Link=…>` — `ExpectedRecordCounts`, and `MockLiveGateway` with the four types it
needs. A project reference was the obvious alternative and is the wrong one: it drags xunit and
`Microsoft.NET.Test.Sdk` through an ILC compile, and neither is trim-safe. A local copy is worse
still, and not for effort reasons — the probe's entire claim is that the native binary reaches *the
same* answers as the managed suite, and two copies of the record-count table make that comparison
vacuous the first time they drift. So the 71 counts moved out of `DbnDecoderTests` into
`ExpectedRecordCounts.cs`, which both programs now compile. It is the same one-file-two-projects
arrangement CLAUDE.md already prescribes for `Internal/ZstdDecompressor.cs`, and it is why a third
loopback gateway was not written: the mock and the real gateway are two implementations of the live
protocol already, and CLAUDE.md's argument against a third is not about effort.

**`TimeseriesClient.OpenFileAsync` is zstd-only, which the probe found by handing it a plain
`.dbn` ([#64]).** It wraps the file in the decompressor unconditionally rather than sniffing for a
frame, and that is correct — it is documented as opening what `GetRangeToFileAsync` writes, and that
is always zstd. Recorded because it is the historical package's one offline entry point, so it is the
one a sample or a probe reaches for first, and "unknown frame descriptor" is not a message that
explains itself.

**What the probe reaches, and why each thing is on the list ([#64]).** ILC compiles only what it can
reach, so a package that is merely *referenced* is trimmed away entirely and proves nothing. Every
check exists to reach into one of the four. The corpus decode is the milestone's stated claim — 71
fixtures against the counts upstream's CLI reports. `RecordRef.Get<T>` over twelve record structs is
the AOT-specific half of it: static abstract interface members dispatching generically over value
types, with each struct's `Unsafe.SizeOf<T>()` cross-checked against its own declared `WireSize` so
no size table is hand-copied. The ten reference code tables go through the same construct, all ten
instantiations forced through one generic method. The source-generated JSON contexts are `internal`
and so cannot be called directly at all — the only way to reach them is to make the real clients
perform a real request, which is what the loopback HTTP socket is for, and a context that failed to
survive trimming would throw `NotSupportedException` at the first deserialize with nothing wrong at
compile time. And a full live session runs against the mock gateway, plain and zstd, because the
async read seam projecting `AlignedBuffer`'s `ulong[]` as a `Memory<byte>` through a
`MemoryManager<byte>` (#15) is the most AOT-exotic thing in the repository and had never run under
ILC.

**Result: 262 checks, zero IL2xxx/IL3xxx, a 9.4 MB `osx-arm64` binary that runs in 21 ms ([#64]).**
The workflow runs `linux-x64` on every push and pull request rather than nightly — AOT compatibility
is broken by a source change, so the run that matters is the one on the change that broke it. One OS
rather than CI's three: ILC is the same compiler everywhere and reads the same IL, so a trim problem
in this library shows up identically on all of them; what differs per platform is the native linker,
which is the toolchain's business. `osx-arm64` is covered by `tools/aot-probe.sh` defaulting to the
host RID. Windows is the gap, and closing it is a `strategy.matrix` block if it ever costs anything.

**A third `$(ShippingProject)` exclusion, and it is not the same as the second ([#64]).** The probe
*wants* the trim and AOT analyzers — `PublishAot` turns them on for itself — so unlike the benchmark
project it is not excluded from those. What it must be excluded from is [#63]'s API lock: it ships no
package, and it compiles the mock gateway, so RS0016 would demand a public API baseline for a few
hundred members of a test double. Three exclusions rather than an opt-in list is still the right way
round: a shipping project that forgot to opt in would silently have no lock and no AOT analysis and
nothing would say so, where a non-shipping project that forgets to opt out fails on its first build.

**RS0026 is suppressed for one file, with the hazard written out ([#63]).** "Do not add multiple
public overloads with optional parameters" fires on `MetadataDecoder.Decode`, which has two —
`ReadOnlySpan<byte>` and `Stream`, each with the same optional `VersionUpgradePolicy`. The rule
guards against a call site silently rebinding when an overload is added or removed, which needs two
overloads one argument list can match; neither of these types converts to the other, so every call
site has exactly one candidate and always will. The documented alternative is four methods for two
operations, which is the right trade for published binaries whose callers cannot recompile and buys
nothing here. Scoped to that file in `.editorconfig`, so an overload set that genuinely *can* be
ambiguous still fails the build.

**Four samples, and the API needed no change to write them ([#66]).** That was the question [#66]
existed to ask — samples land after the lock precisely so that an awkward two-step shows up while the
surface can still move — and the answer is on the record rather than assumed. Every careful path a
sample wanted was already one call: `GetRangeParams.ToQuery()` prices the request that is about to be
sent rather than a second one assembled by hand, `ResolveParams.FromQuery(request)` resolves exactly
the window the records come from, `Resolution.ToSymbolMap()` turns the answer into the lookup, and
`DateRange.OnDay(date).ToDateTimeRange()` crosses from a calendar day to nanosecond instants.

One step was weighed and deliberately not removed. A consumer on the allocating path
(`ReadRecordsAsync`, `RecordsAsync`) holds an `OwnedRecord` and must call `.AsRef()` to hand it to
`TsSymbolMap.TryGetSymbol` or `PitSymbolMap.OnRecord`, which take `RecordRef`. An `OwnedRecord`
overload on each would remove the call, and would also blur the one distinction this library is built
around — that a `RecordRef` points into the buffer and an `OwnedRecord` does not. It is one
discoverable method named for what it does, so it stays, and this paragraph is the record that it was
looked at rather than missed.

**`samples/Directory.Build.props` must import the root one explicitly, and forgetting is silent
([#66]).** MSBuild walks up from a project directory and stops at the *first* `Directory.Build.props`
it finds, so introducing one under `samples/` takes those projects out of everything the root file
sets — `net10.0`, `Nullable`, `TreatWarningsAsErrors`, and the `BannedApiAnalyzers` rule that keeps
BCL `DateTime` out of this codebase. The samples would still have built. The
`$([MSBuild]::GetPathOfFileAbove(…))` chain at the top of that file is what makes them the same
projects as everything else, and the comment above it says so.

**A fourth `$(ShippingProject)` exclusion, and unlike the third it is a plain one ([#66]).** The probe
is excluded from [#63]'s lock but *wants* the AOT and trim analyzers. The samples want neither: what
an analyzer would check on a sample is that the four packages survive being called from analysed
consumer code, and `tools/DatabentoDotNet.AotProbe` answers that by publishing a native binary rather
than by asserting it in a project that is never published. What the lock would cost is eight empty
`PublicAPI.*.txt` files — RS0016 needs both to exist before it will report that a file of top-level
statements has no public surface.

**Being in `DatabentoDotNet.slnx` *is* the CI coverage ([#66]).** The samples cannot be run in CI: no
runner has a key and none should. Building them is the only guarantee available, and it is the one
that matters, because each compiles against the public surface in the working tree — a sample outside
the solution rots the first time the API moves under it. No workflow change was needed, and that is
the point.

**A duplicate nobody could see until a second compiler read the project ([#67]).** Standing up
DocFX produced eight warnings before a single page was written — two per package, "Duplicate source
file `PublicAPI.Shipped.txt`". The cause was real rather than a DocFX quirk:
`Directory.Build.targets` added both baseline files as `AdditionalFiles` explicitly, and
`Microsoft.CodeAnalysis.PublicApiAnalyzers`' own `buildTransitive` targets already add them,
`Condition="Exists(…)"`. csc tolerates the duplicate silently; Roslyn's `MSBuildWorkspace`, which
DocFX loads projects through, does not. Removing our copy was checked against the thing it exists
for rather than assumed safe — with the lines gone, deleting one entry from
`PublicAPI.Unshipped.txt` still fails the build with RS0016 naming the symbol.

**Removing it took a guarantee with it, so the guarantee was written down ([#67]).** The explicit
include was unconditional, so a shipping project with no baseline files failed with `CS2001: Source
file could not be found` — loud, but silent about *which* rule it was serving. The package's include
is conditional, so with ours gone such a project would simply have had no lock and nothing would
have said so. `EnsurePublicApiBaselineExists` now checks the condition on purpose and its message
names the rule, the two files, and the four `IsXProject` properties that opt a project out.
Verified in both directions: the error fires with a file removed, and the full build is still 0
warnings with both present.

**`--warningsAsErrors` changes the exit code, not the summary ([#67]).** DocFX prints "Build
succeeded with warning" and "0 error(s)" whether or not the flag is passed. What changes is the
process exit code — 255 with it, 0 without — which is what a workflow step reads. Measured by
breaking a cross reference on purpose rather than inferred from the flag's name, and the workflow
carries a comment saying not to "fix" the step by grepping the log for the word *warning*.

**Two gates for two kinds of broken link, and neither covers the other ([#67]).** A broken
`<see cref>` in a source XML comment never reaches DocFX at all: it is `CS1574` at compile time and
`TreatWarningsAsErrors` has made it an error since M0 — confirmed by planting one. A broken
`<xref:…>` or file link in a hand-written page is invisible to the compiler and is caught only by
DocFX's `UidNotFound` and `InvalidFileLink`. Both were verified by deliberate breakage; the
issue's "broken links fail the build" is met by two independent mechanisms, not one.

**The site shares a directory with `docs/plans/`, so its content is enumerated ([#67]).** The
reflexive `content` glob is `**/*.md`, and under this layout that would have published the next
implementation plan somebody wrote. `docs/docfx.json` lists its content files instead and says why
in a comment. The cost is one line per new prose page; the alternative is a working document going
public the day after it is written, which is not a failure anyone would notice in review.

**The publish gate was built, then removed the same day, and that is the right order ([#67]).**
The repository was private when this workflow was written, so enabling GitHub Pages would have made
the site publicly reachable from a repository nobody could read — a disclosure decision rather than
a build one, and not one to take by merging a workflow. So `deploy` was conditioned on a
`vars.PUBLISH_DOCS` repository variable and skipped until somebody set it. The repository was then
made public, and the condition was **deleted rather than satisfied**: a gate whose stated reason no
longer holds is one the next reader has to re-derive, and an unset variable would have left the job
silently doing nothing. What survives is the split the gate was hung on — `build` runs on every push
and pull request, `deploy` only from `master` — so a pull request that breaks a cross reference
fails without that branch ever reaching the live site.

**The API reference cost nothing to produce, and that was the plan ([#67]).** 219 pages across
eight namespaces, generated from XML documentation that has been mandatory since M0 — this milestone
was decomposed into six issues rather than nine on exactly that basis, and the estimate held.

**The site itself was the wrong work, and [#70] retired it.** What follows is the sequence, because
one issue reversing another twice in an evening is worth explaining rather than hiding behind three
green checkmarks.

> **Superseded by [#78], [#80] and [#82].** The site exists, is published, and is now the
> documentation: the wiki's ten guides moved into `docs/` and the wiki was retired. This section is
> kept as written rather than corrected, because the *rule* it argues from is still the governing
> one — exactly one canonical copy of each fact. What changed is which surface holds that copy, and
> the reasoning below is why the question was worth asking twice. See the M5 entry below.

**The seven prose pages were the wrong work, and [#69] deleted all of them.** The wiki had been
written two days earlier and already carried the guides — `Zero-Copy-and-Allocation` and
`Timestamps-and-Prices` are supersets of what [#67] wrote, and its `Wiki-Style-Guide` states the
division of labour outright: guides and explanations in the wiki, API shape in the repository,
repository conventions in `CLAUDE.md`. The seventh page, on testing conventions, duplicated
`CLAUDE.md`'s own Testing section down to two verbatim headings.

**What made it easy is worth naming, because it will recur.** A GitHub wiki is a *separate git
repository*. It is invisible from the working tree, absent from `git status`, and does not appear
in any file listing of the project — so a reader of this repository, human or otherwise, has no
signal that documentation already exists. It was a sibling checkout at `../databentodotnet.wiki`
the whole time. `CLAUDE.md` now carries a table of which documentation goes where and names that
path, which is the only durable fix available: the duplication cost nothing but the writing, and
the next one would cost the same again.

**Then [#70] removed the API reference too, and the wiki had already written down why.** Its style
guide says the reference *is* the doc comments and the IDE *is* the browser, "and neither can drift
from the code because both **are** the code". A DocFX rendering does not drift either, being
generated — but it is a second surface to host, publish, link and keep reachable, for a fact the
reader already has at the call site. `dotnet pack` ships the `.xml` documentation file inside each
package, so IntelliSense carries every comment into the consumer's editor with nothing published at
all.

**What the site cost before it was removed is the argument, stated as evidence.** Two hours produced
a domain decision, a Cloudflare investigation, a stale `CNAME` in an unrelated repository, a
branch-protection lockout, and a URL that never resolved for anybody. It produced no documentation
that did not already exist. The three issues are not three mistakes; they are one question — *where
does documentation live* — answered in stages because nobody asked it before writing.

**Two things from [#67] outlived it, and are not to be reverted with the rest.** The duplicate
`AdditionalFiles` removal and `EnsurePublicApiBaselineExists` in `Directory.Build.targets` are a real
defect fix that predated the site; the site is only what surfaced it, and removing the target would
restore a silent hole in [#63]'s lock. So is `CLAUDE.md`'s table of where documentation goes, which
is the durable output of the whole sequence.

**The latency benchmark is a test, not a BenchmarkDotNet benchmark ([#65]).** §7 lists it under
the benchmarks and [#28]'s project was the obvious home, and it is the wrong one. BenchmarkDotNet
runs a workload repeatedly with warm-up and reports the distribution *of the operation* — so each
iteration would start its own billable session, and the figure produced would be the mean cost of
"stream for a minute" rather than the percentiles of the record latencies inside one stream. The
issue's own gate is the tell: `Category=Live` plus `DATABENTO_LIVE_SESSION` is xUnit's vocabulary,
not BenchmarkDotNet's, and `CLAUDE.md`'s free/billable split is a table of test *files*. So it lands
as `RealGatewayLatencyTests` beside `RealGatewaySessionTests`, and the benchmark project is left
measuring the things that can be measured offline.

**Three series rather than one, because one number cannot be honest about a clock it does not own
([#65]).** The headline is `ts_out -> delivered`, and it spans two machines whose clocks are not
synchronised — so a constant offset is added to every observation in it, and one-way latency between
unsynchronised clocks is not observable at all. Two things follow, and both are in the report.
Negative observations are printed rather than clamped, because a negative latency is the only direct
evidence the measurement has that the clocks disagree, and replacing it with zero would substitute a
plausible figure for a finding — the failure this repository's date handling exists to prevent, in a
different unit. And every row carries `p99 - min` beside its absolute figures: an offset cancels out
of any difference between two observations in the same series, so the spread survives what the
absolutes cannot. The other two rows are skew-free by construction and bracket the headline —
`ts_recv -> ts_out` is the gateway timing itself against its own clock, and `buffer read ->
delivered` is this library's own decode and drain cost on one monotonic clock.

**The measurement was split from the session that pays for it, and that is what made it testable
([#65]).** `RealGatewayLatencyTests` is session setup and three assertions; the collection loop, the
exclusion rule, the clock and the report live in `LatencyMeasurement` and `LatencyStatistics`, which
`MockLiveGateway` drives on every `dotnet test`. The mock cannot say what the latency is — over
loopback that number is about loopback — but it settles everything else, and one detail makes the
central check exact: the mock stamps `ts_out` from an injected `IClock`, so putting that clock five
seconds from ours turns "is this latency plausible?" into an answer known before the test runs.
Verified by breaking it: reading `IndexTs` instead of `TsOut` puts the result three years out and
fails two tests, rather than surfacing in the one run that needs an open market.

**Heartbeats are excluded from the sample, and that is a measurement decision rather than tidiness
([#65]).** `RealGatewaySessionTests` asks for a five-second heartbeat interval precisely so it passes
at 3am on a closed market; this one asks for none. Gateway-generated records — heartbeats, errors,
the symbol mappings at the head of a session — arrive on an *idle* socket, which is the best case the
transport ever has, so a quiet session's worth of them mixed into a sample of market data pulls p50
toward a figure no consumer will ever see. They are counted and reported separately instead.

**The report's own formatting was a defect, found by reading the rendered output rather than the code
([#65]).** Columns were right-aligned with `PadLeft`, which does nothing to a string already at the
column width — so two wide figures ran together into one unreadable number and took the table's
alignment with them for every row below. It surfaced immediately on the mock, whose synthetic records
carry 2023 timestamps against a 2026 clock. The values that overflow are exactly the ones worth
reading, since a latency orders of magnitude out is either the finding or the bug, so an over-wide
cell now takes a leading space and pushes the row out. Recorded because the alternative was finding
it in the one run that costs money and needs a trading session.

**What is asserted, given that #65 says "reported, not asserted" ([#65]).** No latency threshold —
that would be a flake generator over a network path, and this cannot run in CI to flake in anyway.
What is asserted is that the measurement *is* one: that the session negotiated `ts_out`, that the
drain series never goes backwards (both its stamps come from one monotonic clock, so a negative there
is a broken instrument rather than a slow network), and that the sample reaches 100 observations —
below which nearest rank puts p99 on the last element and the figure is the maximum wearing a
percentile's name. That last one fails with a message naming the likely cause, because a closed
market is the usual reason and a run that printed a p99 over forty records would look exactly as
credible as a real one.

**`OpenFileAsync` being zstd-only bit a second time, exactly where §7 predicted ([#66]).** The [#64]
entry above notes that `TimeseriesClient.OpenFileAsync` decompresses unconditionally and is therefore
the wrong thing to hand a plain `.dbn`. The batch sample is the first consumer-position code to reach
for it, and it works only because the job it submits sets `Compression.Zstd`. Left at the default the
sample would have downloaded fine and then failed on "unknown frame descriptor", which is the failure
that entry describes. Written down twice on purpose: one prediction and one occurrence.

**The predicted clock skew arrived, and chasing it is what found the better measurement
([#65], [#83]).** The entry above argues that `ts_out -> delivered` spans two clocks and that a
negative observation is the only direct evidence they disagree. Both real runs on 2026-08-31 produced
one: a transport median of **-29.5 ms**, then **-23.2 ms** — negative through the median, so the
disagreement is the whole row rather than a tail effect, and the decision not to clamp is what turned
it into a finding instead of a plausible-looking zero. NTP put this machine **70 ms, then 63 ms**
behind UTC, which is both the sign and the magnitude the figures require; adding it back gives a
transport p50 of 40.8 ms and 40.2 ms respectively. That two runs with *different* offsets agree to
within a millisecond is the corroboration, and it is still an inference: it assumes the gateway's
clock is true, and NTP's own estimate assumes a symmetric path — an assumption its two servers
undercut by disagreeing 3.5 ms with each other. **The raw table is what the checklist records,
because it is what was measured.**

**A clock is a ruler, and a ruler only measures a length if both ends are read off the same one
([#83]).** That is the whole of it. `drain` reads both its stamps from one `Stopwatch`, so the
anchor cancels in the subtraction and the row is exact — which is why it returned **7.7 µs at p50 in
both runs**, on different samples, different feed rates and different clock offsets. `gateway
internal` reads both stamps off Databento's clock and is exact for the same reason. `transport` reads
one end off each, so what it yields is the interval *plus the distance between the two zeros*. It is
not a defect to be fixed: one-way delay between unsynchronised clocks is not observable at all, and
the only quantities a single clock can observe are intervals on itself and round trips. So the
library's own number was never the headline row, and [#83] measures the round trip that is the
observable form of the same question — reaching **37.3 ms** one-way against ~40 ms from the corrected
transport row, by a method that reads no wall clock at all.

**The report now leads with `drain`, and that is a correction rather than a preference ([#83]).**
It previously labelled `transport` "THE HEADLINE". That row is neither this library's cost nor
Databento's — it is how far the measuring machine sits from the gateway, and it would read the same
for the Python, C++ and Rust clients. Presenting geography as a client-library metric is the kind of
confident wrong number this repository's date handling exists to prevent, so the rows are ordered
`drain`, `gateway internal`, `transport`, and the explanatory block says which of the three each
party owns. The one test that indexed a row by position broke on the reorder and now selects by
name, which is what it should have done.

**`RecordTarget` never bound, and the deadline did ([#65]).** The harness asks for 20,000 records or
60 seconds, whichever trips first, on the assumption that eight megacaps and SPY are a firehose. Five
minutes after the open they delivered **879 trades in 60 s** — about fifteen a second — because
`EQUS.MINI` is a consolidated *mini* feed rather than a full venue tape. Nearest rank then put p99 at
the 871st of 879, so the reported p99 and max rested on eight observations while the p50 was solid.
The budget is the only one of the two bounds that binds on this feed, so it is the only one that can
fatten the tail: raised to five minutes, the second run collected **2,240 records** and put
twenty-two above p99. The cap is kept where it is, as a ceiling a busier feed could actually reach.

### Checklist

- [x] Benchmarks (BenchmarkDotNet): records/sec decode, allocations/record.
  *(`benchmarks/DatabentoDotNet.Benchmarks`, landed early in [#28] rather than waiting for M5,
  because M2's definition of done requires the allocation figure and nothing measured it. Not in
  the CI test run and not packable — see the project file for the two properties that arrange
  that, neither of which is optional. The **enforcement** is separate and deliberately so:
  `AllocationTests` and `LiveAllocationTests` assert exactly zero bytes per record on every
  `dotnet test`, over the whole 71-fixture corpus and over the mock gateway's socket. A benchmark
  someone has to remember to run cannot hold a guarantee.)*
- [x] Public API surface locked via `Microsoft.CodeAnalysis.PublicApiAnalyzers` — [#63].
      3,801 entries across the four packages, in `PublicAPI.Unshipped.txt` through the `0.9.0` beta
      ([#74]) and moved across by [#68] at 1.0.0, which is when the surface is promised.
- [x] Native AOT compatibility verified end-to-end — [#64].
      `tools/DatabentoDotNet.AotProbe` publishes with `PublishAot` and *runs*: 262 checks, zero
      IL2xxx/IL3xxx, the 71-fixture corpus decoded to the counts `DbnDecoderTests` asserts, both
      HTTP clients' source-generated JSON contexts exercised over a loopback socket, and a full live
      session — plain and zstd — over the mock gateway. `tools/aot-probe.sh` runs it; the
      `Native AOT` workflow runs that on every push.
- [x] Live end-to-end latency benchmark — [#65]. **Measured 2026-08-31 over `EQUS.MINI` `trades`**
      for AAPL, MSFT, NVDA, AMZN, META, GOOGL, TSLA and SPY, DBN version 3 with `ts_out` negotiated.
      Two runs: 879 records in 60.1 s at 09:35 EDT, then **2,240 records in 300.3 s at 10:16 EDT**,
      which is the one recorded here. Microseconds, nearest rank:

      ```
      series                                      n         min         p50         p99         max
      drain (buffer read -> delivered)         2240         0.5         7.7        27.4        88.8
      gateway internal (ts_recv -> ts_out)     2240         4.0       512.5      8927.2     16844.7
      transport (ts_out -> delivered)          2240    -24068.1    -23240.5     74320.5    461311.3
      ```

      **`drain` is this library's own cost and the only row it owns** — decode plus the wait behind
      earlier records of the same buffer read. Both stamps come from one `Stopwatch`, so no clock
      offset can enter, and it returned **7.7 µs at p50 in both runs**. **`gateway internal` is
      Databento's**, on Databento's clock. **`transport` is neither**: it spans two machines' clocks,
      its median is negative because this machine's was 63 ms behind UTC, and what it reports is the
      distance from London to a US gateway — see the three entries above, and [#83] for the same
      distance measured without a wall clock. `RealGatewayLatencyTests` runs the session behind
      `DATABENTO_LIVE_SESSION` on top of `Category=Live`; `LatencyMeasurement` and
      `LatencyStatistics` are driven by `MockLiveGateway` on every `dotnet test`. Needs a real
      gateway, so it is the one benchmark that cannot run in CI; see the two-surface argument in §4.
- [x] Gateway round-trip time — [#83]. **Measured 2026-08-31 against `EQUS.MINI` at
      `209.127.154.235:13000`**, ten connections:

      ```
      series                                      n         min         p50         p99         max
      connect (TCP handshake)                    10     74590.4     75825.6     79467.8     79467.8
      authenticate (greeting + CRAM)             10    171094.4    174980.6    207883.2    207883.2
      ```

      A TCP connect is one network round trip the far-side kernel completes with no application
      involved, so its **minimum, 74.6 ms**, is the best estimate this machine can make of the path;
      the authenticate row is a further round trip *plus* the gateway validating a digest, and is an
      upper bound rather than a measurement of one. Every figure is a difference between two
      `Stopwatch` readings, so **no wall clock is read and no offset can enter** — which is what makes
      it worth comparing with [#65]'s transport row. Halving it gives **37.3 ms** one-way against
      ~40 ms from the offset-corrected transport figure: two independent routes to the same answer,
      one of which never touches NTP. That halving assumes a symmetric path, which is weaker than
      "two wall clocks agree" and is still an assumption — a one-way delay was not measured, and the
      report says so. **Free**: the handshake finishes well short of `start_session`, so it lives in
      `RealGatewaySmokeTests`, runs on a closed market, and leaves CLAUDE.md's free/billable table
      unchanged.
- [x] Samples: live stream, historical range, batch download, symbol resolution — [#66].
      Four console projects under `samples/`, in the solution so CI builds them and cannot run them.
      Each takes its key from `DATABENTO_API_KEY` and nothing else, and each was run against the real
      API before this closed: the live one replayed 20 EQUS.MINI trades through
      `FillBufferAsync`/`TryNextRecord`, the historical one priced a range at `$0.000012516975` and
      took it, the batch one submitted job `GLBX-20260829-HUA6PJTG7V` and decoded the file it
      downloaded, and the symbology one resolved `ESH4`/`ESM4` and named the instrument id on every
      record it read.
- [x] Documentation — [#67], [#69], [#70], [#78], [#80], [#82]. **Resolved as: the site is the
      documentation.** [#67] built a DocFX site, [#69] cut it to the generated reference once the
      wiki turned out to already hold the prose, and [#70] retired what was left — all inside one
      evening. [#78] rebuilt it once the reference carried `<example>` blocks, on the argument that a
      worked example is what a reader wants *before* they have the package installed. [#80] published
      it, which none of the previous three had managed, after finding the cause was an account-level
      Pages redirect rather than anything in this repository. [#82] then moved the wiki's ten guides
      into `docs/` and retired the wiki, leaving one surface and one copy of each fact.
      `CLAUDE.md` carries the table saying what goes where. The original ROADMAP line — "XML docs on
      all public API; DocFX site" — turned out to be right on both halves, after four issues argued
      otherwise; the XML half has been complete since M0.
- [x] `0.9.0` beta — [#74]. **Published 2026-08-30**, all four packages, tagged `v0.9.0` on
      9827535 and released by the `release: published` trigger (run 33303094547). Verified against
      the *published* artefacts rather than a local pack: each `.nupkg` downloaded back off the feed
      carries `projectUrl`, `readme`, `icon`, `releaseNotes`, the Apache-2.0 expression, and
      `LICENSE`/`README.md`/`icon.png` as files — the four pieces of metadata `0.1.0-alpha` shipped
      without ([#71] read its nuspecs and found them absent). All four install from a clean feed into
      a fresh project with `NUGET_PACKAGES` pointed at an empty directory, compile, and *run*; the
      resolved closure is exactly eight packages — our four, NodaTime, ZstdSharp.Port,
      `Microsoft.Extensions.Logging.Abstractions` and the `DependencyInjection.Abstractions` the last
      of those declares. All four PDBs come back from `symbols.nuget.org`.

      Each package carries its own README rather than a copy of this repository's, whose relative
      links resolve on github.com and 404 on nuget.org. Their code samples are **compiled against the
      real assemblies** rather than proofread, which caught three wrong ones before they became
      permanent on a package page: `FillBufferAsync` returns `ValueTask<int>` and not a `bool`,
      `SecurityMaster` exposes `Symbol` and not `RawSymbol`, and the Reference snippet needed a
      `using` for `SType`.

      `publish.yml`'s two defects are fixed, and the fix earned itself on its first real run. The
      version is read off the packed artefact — the log opens `Packed version: 0.9.0` where a
      hardcoded `0.1.0-alpha` used to sit — and a pre-flight against the flat container refuses a run
      that would publish nothing, which is what run 33280134279 silently was. A third defect turned
      up while fixing them: the step named "List packages on NuGet.org" POSTed to
      `/api/v2/package/{id}/{version}` with the API key, which **relists** a version rather than
      listing anything, so with its hardcoded version it would have quietly relisted an old
      prerelease on every future release. It is now a read-only check — and **that check took five
      minutes to go green** (09:04:23 → 09:09:25), so a single post-push assertion would have failed
      this release spuriously. The retry loop was not defensive padding.

      The `DatabentoDotNet` ID prefix is **reserved** — granted 2026-08-31, exclusive to owner
      `jerbersoft`, and the one part of this item that was never a repository change. All four
      packages return `"verified": true` from nuget.org's search API, which is the flag its package
      pages render as the reserved-prefix indicator. That flag is true under a *public* prefix too
      — the weaker outcome [#74] named as its fallback — so it establishes that the indicator is
      live and the grant establishes that it is exclusive; neither says both.
- [ ] NuGet publish + release automation, `0.x` → 1.0.0 — [#68]. **Gated on what the beta finds.**
      The mechanism is done, the metadata is done, and 0.9.0 proved both on a real release; what
      1.0.0 adds is the promise — moving the 3,801-entry baseline into `PublicAPI.Shipped.txt`, where
      RS0017 makes every later removal a build failure. That promise is cheap to make and expensive
      to withdraw, and until 0.9.0 nothing had built against this library in anger. It is now
      installable by anyone, which is the evidence [#68] was waiting for and cannot be hurried.

[#28]: https://github.com/jerbersoft/databentodotnet/issues/28
[#58]: https://github.com/jerbersoft/databentodotnet/issues/58
[#63]: https://github.com/jerbersoft/databentodotnet/issues/63
[#64]: https://github.com/jerbersoft/databentodotnet/issues/64
[#65]: https://github.com/jerbersoft/databentodotnet/issues/65
[#83]: https://github.com/jerbersoft/databentodotnet/issues/83
[#66]: https://github.com/jerbersoft/databentodotnet/issues/66
[#67]: https://github.com/jerbersoft/databentodotnet/issues/67
[#68]: https://github.com/jerbersoft/databentodotnet/issues/68
[#69]: https://github.com/jerbersoft/databentodotnet/issues/69
[#70]: https://github.com/jerbersoft/databentodotnet/issues/70
[#71]: https://github.com/jerbersoft/databentodotnet/issues/71
[#74]: https://github.com/jerbersoft/databentodotnet/issues/74
[#78]: https://github.com/jerbersoft/databentodotnet/issues/78
[#80]: https://github.com/jerbersoft/databentodotnet/issues/80
[#82]: https://github.com/jerbersoft/databentodotnet/issues/82

---

## 8. Sequencing

```
M0 Foundation ──> M1 Dbn codec ──┬──> M2 Live       (PRIORITY 1) ──┐
                                 └──> M3 Historical (PRIORITY 2) ──┤
                                                                   ├──> M5 Polish ──> 1.0
                                      M4 Reference (JSONL, no dep on M1) ──┘
```

M1 is the bottleneck for M2 and M3 and cannot be parallelised away — both transports are just
DBN over different pipes. Within M1, enums (1a) and records (1b) can proceed in parallel with
the metadata/decoder work (1c/1d) once the header layout is fixed.

**M4 (Reference) is the exception: it does not depend on the DBN codec at all** (zstd-JSONL),
so it can be built any time after M0 — useful as parallel work whenever M1 is blocked.

---

## 9. Open questions

1. ~~**TFM**~~ — **RESOLVED:** `net10.0` only. Multi-targeting was tried and reversed in #16. See §0.
2. ~~**Scope of 1.0**~~ — **RESOLVED:** full parity with `databento-rs` (Live + Historical +
   reference data). See §6.
3. ~~**Package IDs / namespaces**~~ — **RESOLVED:** `DatabentoDotNet.*` throughout. See §1.
4. ~~**.NET 11 preview SDK**~~ — **RESOLVED:** not installing locally, and as of #16 there is
   no longer a target that needs it. See §0.
5. **API-key handling** — env var (`DATABENTO_API_KEY`) by default, matching the other clients?
