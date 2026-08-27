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
- [ ] Publish placeholder packages to claim the `DatabentoDotNet.*` IDs before someone else does.

**Definition of done:** ✅ `dotnet build` green, `dotnet test` green (3/3), CI defined.

---

## 3. Milestone 1 — `DatabentoDotNet.Dbn` codec

**Status: complete** on branch `m1-dbn-codec` (24 commits, 789 tests, zero warnings),
with #16 (dropping the `net11.0` target) folded in. See #11 for the one M1 item still open.

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

[#20]: https://github.com/jerbersoft/databentodotnet/issues/20
[#21]: https://github.com/jerbersoft/databentodotnet/issues/21

### Client surface
Mirror `databento-rs`: `ConnectAsync`, `Subscribe`, `StartAsync` (returns `Metadata`),
`FillBufferAsync`, `TryNextRecord`, `ReconnectAsync`, `ResubscribeAsync`, `CloseAsync`,
plus a builder. `ReconnectAsync` + `ResubscribeAsync` as **separate** operations is a
deliberate upstream choice — replaying subscriptions is a caller decision, not automatic.

Upstream's `next_record()` is deliberately **absent**: it returns a `RecordRef<'_>` from an
`async fn`, which C# cannot express at all. `FillBufferAsync` + `TryNextRecord` is the port of
its `fill_buf()` / `try_next_record()` pair, and the ergonomic `IAsyncEnumerable<T>` (which
copies) is what most callers will use instead. See #15.

### Details that bite
- **Heartbeats** arrive as `SystemMsg` records, not control frames. `heartbeat_interval_s` is
  opt-in. Use it as a liveness signal and surface a configurable read timeout.
- **`SlowReaderBehavior`** — `Warn` (gateway warns, keeps sending) or `Skip` (gateway drops
  records to catch you up). Expose it; a slow .NET consumer is a realistic failure mode.
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

**Definition of done:** an integration test against a mock gateway (upstream ships one in
`live/client.rs` tests — port the shape), plus a live smoke test against a real dataset,
sustaining an MBO stream with zero per-record allocation on the low-level path.

---

## 5. Milestone 3 — `DatabentoDotNet.Historical`

> Tracked by [#7](https://github.com/jerbersoft/databentodotnet/issues/7) · milestone `M3: Historical`

Base URL `https://hist.databento.com`, paths `v0/{slug}` (`API_VERSION = 0`), **HTTP Basic auth with
the API key as the username and an empty password**. Honour the `X-Warning` response header
(surface as warnings) and log `request-id` on every error — it is what support will ask for.

Endpoints, grouped as upstream does:

- **`timeseries.get_range`** — the main event. Streams DBN over HTTPS; reuse the M1 decoder
  directly. Also `get_range_to_file`.
- **`metadata.*`** — `list_publishers`, `list_datasets`, `list_schemas`, `list_fields`,
  `list_unit_prices`, `get_dataset_condition`, `get_dataset_range`, `get_record_count`,
  `get_billable_size`, `get_cost`.
- **`symbology.resolve`**.
- **`batch.*`** — `submit_job`, `list_jobs`, `list_jobs_full`, `get_job_details`,
  `list_files`, `download`, `download_file`.

Notes: cost/billing endpoints should be prominent in docs — users want `get_cost` *before*
`get_range`. Batch download needs resumable, parallel file transfer and integrity checks.

**Definition of done:** every endpoint covered, `get_range` streaming a multi-GB range with
flat memory, batch download resumable across process restart.

---

## 6. Milestone 4 — `DatabentoDotNet.Reference` (1.0 blocker)

> Tracked by [#8](https://github.com/jerbersoft/databentodotnet/issues/8) · milestone `M4: Reference data`

**DECIDED: 1.0 is full parity with `databento-rs`** — Live + Historical + reference data.

A separate client (`reference.rs` upstream), same `hist.databento.com` host and Basic auth,
but **responses are zstd-compressed JSONL, not DBN** — this needs its own response handler,
not the M1 record decoder. Requests are POSTs with form-encoded bodies.

- [ ] `security_master.get_range`, `security_master.get_last`
- [ ] `corporate_actions.get_range`, `corporate_actions.list_events`, `corporate_actions.list_enums`
- [ ] `adjustment_factors.get_range`
- [ ] Shared zstd-JSONL streaming deserializer (reuse the M0 zstd abstraction; stream, don't buffer)

`list_events` and `list_enums` return schema-describing maps — useful for validating the
strongly-typed models we generate for corporate actions.

**Definition of done:** all six endpoints covered, JSONL streamed with flat memory.

---

## 7. Milestone 5 — Polish & release

> Tracked by [#9](https://github.com/jerbersoft/databentodotnet/issues/9) · milestone `M5: Polish and 1.0`

- [ ] Benchmarks (BenchmarkDotNet): records/sec decode, allocations/record, live end-to-end latency.
- [ ] Native AOT compatibility verified end-to-end.
- [ ] Samples: live stream, historical range, batch download, symbol resolution.
- [ ] XML docs on all public API; DocFX site.
- [ ] Public API surface locked via `Microsoft.CodeAnalysis.PublicApiAnalyzers`.
- [ ] NuGet publish + release automation.

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
