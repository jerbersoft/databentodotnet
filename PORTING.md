# Porting guide: `databento-rs` → `DatabentoDotNet`

Reference sources, read 2026-08-26:

| Source | Version | Location |
|---|---|---|
| `databento-rs` | 0.60.0 | `../databento-rs` (sibling clone) |
| `dbn` | 0.68.0 | separate crate — the codec lives here, **not** in `databento-rs` |
| `databento-cpp` | — | struct-size `static_assert`s, used as layout oracle |

`databento-rs` depends on `dbn = "0.68"`, so the two are version-matched. Milestone 1 (the
codec) ports from **`dbn`**; milestones 2–4 port from **`databento-rs`**.

> **Rule for this port:** the Rust source is authoritative for *functionality, wire format, and
> behavior*. It is **not** authoritative for structure. Where a Rust construct exists only to
> satisfy the borrow checker or to work around a missing language feature, use the .NET
> equivalent instead — those cases are called out individually below.

---

## 1. Constructs to port *structurally* (the design is good and language-neutral)

### `AlignedBuffer` → `AlignedBuffer`
`dbn/src/decode/dbn/aligned_buffer.rs`. Backed by `Box<[u64]>` specifically to guarantee 8-byte
alignment, because records are reinterpreted in place.

**.NET equivalent: allocate `ulong[]` and view it as bytes via `MemoryMarshal.AsBytes<ulong>()`.**
This is the exact analogue of `Box<[u64]>` — 8-byte aligned by construction, GC-managed, no
pinning, no unsafe allocation. (`GC.AllocateArray<byte>(n, pinned: true)` also works but is
weaker: pinned-heap alignment is an implementation detail, not a guarantee.)

Port the semantics verbatim, including the deliberate one:

- `position` / `end` indices; `Data` = `[position..end]`, `Space` = `[end..capacity]`
- `Consume(n)` and `Fill(n)` **only move indices — they never memmove**
- Shifting is **explicit** (`Shift`, `ShiftForSpace(needed)`), so the copy is paid once at a
  refill boundary rather than once per record. Do not "simplify" this into an implicit shift.

### The decoder state machine → `DbnFsm`
`dbn/src/decode/dbn/fsm.rs`. States: `Prelude` → `Metadata{length}` → `Record`.
Default buffer 64 KiB. This handles partial reads at arbitrary byte boundaries, which a TCP
socket will absolutely produce — it is not optional complexity.

### `fill_buf()` + `try_next_record()`
`databento-rs/src/live/client.rs`. Upstream's low-level API already separates *async I/O* from
*synchronous record extraction*:

```
loop {
    while let Some(record) = client.try_next_record()? { process(record); }
    if client.fill_buf().await? == 0 { break; }
}
```

**This shape ports 1:1, and the compiler checks the part that matters.** A `RecordRef` local is
legal inside an `async` method; what C# rejects is one that *survives* an `await` — CS4007,
"cannot be preserved across 'await' or 'yield' boundary". That is exactly the lifetime rule
`DbnFsm` already documents by hand (a record is valid only until the next call on the machine),
now enforced at compile time. So the fill and the drain live in one method, as upstream's own
example does:

```csharp
while (true)
{
    while (client.TryNextRecord(out var record)) Process(record);
    if (await client.FillBufferAsync(ct) == 0) break;
}
```

What *is* impossible is **returning** one: no `async` method can return a `ref struct`, so there
is no `Task<RecordRef>`, and upstream's convenience wrapper `LiveClient::next_record()` — one
`await` that hands back a `RecordRef<'_>` — has **no .NET equivalent** on the zero-copy path. The
`fill_buf` / `try_next_record` pair does, and it is what `DatabentoDotNet.Live` will expose. (An
`IAsyncEnumerable<T>` of *copied* records is the ergonomic alternative for callers who do not
need zero-copy; `yield` has the same restriction as `await`, which is why it must copy.)

#### The read seam: `fsm.space()` → `DbnFsm.SpaceMemory()`  (#15)

Upstream writes `reader.read(fsm.space()).await`. The direct translation does not compile:
`DbnFsm.Space()` returns a `Span<byte>`, and there is no `ReadAsync(Span<byte>)` overload in .NET
— there cannot be, since the span would have to live across the await. And there is no one-line
fix, because `AlignedBuffer` is backed by a `ulong[]` for 8-byte alignment and the BCL has **no
`Memory<T>` reinterpret cast** — `MemoryMarshal.Cast` works on spans only.

Three options were on the table. The decision is **(3)**:

1. **Await readiness, then read synchronously** — `await socket.ReceiveAsync(Memory<byte>.Empty)`
   to wait for data, then `socket.Receive(fsm.Space())`. **Rejected: it only works on a raw
   socket, and the live transport is not always one.** A live session negotiates
   `compression=zstd` in its auth string (`live/protocol.rs:285`), and upstream reads through
   `AsyncDynReader<BufReader<ReadHalf<TcpStream>>>` accordingly (`live/client.rs:46`,
   `client.rs:1235`). Under compression the FSM's bytes come out of a zstd decoder: a zero-byte
   `ReadAsync` on a decompressing stream returns 0 immediately and signals nothing, and a
   synchronous `Read` on one can block for many socket round-trips while it accumulates a frame.
   The trick is a property of sockets, not of streams.
2. **A `MemoryManager<byte>` owned by the client.** Right mechanism, wrong owner — see below.
3. **A `Memory<byte>` seam owned by `AlignedBuffer`.** ✅ `AlignedBuffer.SpaceMemory` and
   `DbnFsm.SpaceMemory()`, backed by a private `MemoryManager<byte>` that projects the `ulong[]`
   as bytes. The buffer owns the array and *replaces* it in `Grow()`, so it is the only thing
   that can keep the projection honest: the manager holds the buffer rather than the array and
   resolves it per call, which makes a `Memory` taken before a `Grow` follow the storage instead
   of writing into the abandoned one. A manager owned by the client (option 2) goes stale the
   moment the buffer grows — and the corpus does grow it, which the tests confirm.

`Space()` is now implemented as `SpaceMemory().Span`, so the synchronous and asynchronous views
cannot drift apart.

What option 1 would have bought, for the record: it avoids pinning the 64 KiB buffer across an
idle wait, which is why Kestrel uses it. If that ever shows up in a profile it can be layered on
top for the *uncompressed* path only — it does not replace the seam.

One consequence worth knowing: `Stream`'s *base* implementations of `Read(Span<byte>)` and
`ReadAsync(Memory<byte>, …)` rent a `byte[]` from `ArrayPool` and copy the result across, so any
stream in the read path that does not override them silently adds a full buffer copy per read.
`ZstdSharp.DecompressionStream` overrides all of them, which is what makes a compressed live
session decompress *directly into* the state machine's buffer. `AsyncReadSeamTests` asserts this,
because a package bump could take it away without breaking anything else.

Proof lives in `AsyncReadSeamTests` (the whole 71-fixture corpus over a real loopback socket,
compressed and not, in 7-byte writes so records straddle reads) and in `AlignedBufferTests`
(the pinned address of `SpaceMemory` equals the address of `Space`, and is 8-byte aligned).

---

## 2. Constructs to port *behaviorally* — use .NET patterns instead

### `State::Consume { read, compat, compat_fill, expand_compat }` → **delete it**
This fourth FSM state does not model anything about DBN. It exists solely because Rust will not
let `process()` mutate `self.buffer` while a `data()` borrow is live, so the mutation is
deferred into a state variant and applied on the next loop iteration ("Advance internal buffer
state. Gets around mutability requirements." — its own doc comment).

C# has no such restriction. **Consume immediately and drop the state.** This is the clearest
case in the whole port of structure that must not be carried over.

### `ClientBuilder<AK, D>` type-state → `required` init properties  (#19)
Rust encodes "key and dataset must be set before `build()`" in the type parameters because it
has no required-field concept — `build()` exists only on `ClientBuilder<ApiKey, String>`. C# 11+
says the same thing natively, and `LiveClient` carries the properties itself rather than behind
a separate options record:

```csharp
public sealed class LiveClient : IAsyncDisposable
{
    public required ApiKey ApiKey { get; init; }
    public required string Dataset { get; init; }
    public bool SendTsOut { get; init; }
    public Duration? HeartbeatInterval { get; init; }         // validated in its init accessor
    public SlowReaderBehavior? SlowReaderBehavior { get; init; }
    public VersionUpgradePolicy UpgradePolicy { get; init; } = VersionUpgradePolicy.UpgradeToV3;
    public EndPoint? Gateway { get; init; }
    public Duration ConnectTimeout { get; init; } = Duration.FromSeconds(10);
}
```

Same compile-time guarantee, no generic type-state machinery. Two things the shape buys that the
Rust builder does not:

- **An `init` accessor is a validation site.** `HeartbeatInterval` rejects a value outside the
  gateway's 5–1800 second range, and rejects sub-second precision outright. Upstream documents
  the range and enforces neither: it warns about the fraction and then discards it
  (`live.rs:133-146`), so the interval in the caller's code is not the interval on the wire.
- **`ApiKey` is a type, not a `string`.** Upstream's `ApiKey` exists for the same reason and
  redacts its `Debug` impl — then interpolates the whole key into an `error!` line when the key
  is not ASCII (`lib.rs:250`). That line is not ported. An invalid key is still a key, and very
  often a valid key for a different account.

`Compression` and `AuthTimeout` are absent on purpose: the auth line that carries them is #20.

### `determine_gateway` → `LiveGateway.For`, still without validation  (#19)
Lowercase the dataset, turn every `.` into a `-`, prepend it to `lsg.databento.com:13000`.
`GLBX.MDP3` → `glbx-mdp3.lsg.databento.com:13000`. Plain TCP; there is no TLS anywhere in the
live protocol.

**The dataset is deliberately not checked against the `Dataset` enum** — #19 asked for that call
to be made and written down, and this is it. Upstream validates nothing and says so, and the
reason survives the port: Databento ships datasets faster than a table generated from one release
of `publishers.rs` tracks them, so an enum check would reject a dataset that exists in favour of
one this build happens to know about. Refusing to connect because our table is stale is a worse
failure than a DNS error naming a host that does not resolve.

What *is* checked is that the transformation produced something that could be a DNS label at all:
`a-z0-9-` only, at most 63 characters, no leading or trailing hyphen. That rejects what a typo
produces — a slash, a space, a newline — without ever claiming to know which datasets exist.

**A failed connection has two shapes, and they are different exceptions.** `LiveConnectException`
wraps whatever the socket raised and names the endpoint; `ConnectTimeoutException` derives from
it and means the attempt was still outstanding when the budget elapsed. A closed port produces
the first, not the second — TCP answers a SYN to a closed port with a RST, so the attempt fails
at once. #19's definition of done asked for a timeout there; that is not how TCP behaves, and
the two cases are tested separately instead.

### `Result<T, E>` → exceptions, with `Try*` for expected outcomes
Rust returns `Result` for everything. C# should not. Split by whether the case is *expected*:

| Rust | .NET |
|---|---|
| `Err(Error::Auth(..))`, `ConnectTimeout`, `AuthTimeout` | throw |
| `Err(Error::BadArgument{..})` | `ArgumentException` / `ArgumentOutOfRangeException` |
| `Err(Error::Dbn(..))` on malformed input | throw `DbnException` |
| `Ok(None)` from `next_record` (stream ended) | `false` from `TryNextRecord` / `0` from `FillBufferAsync` — **not** an exception |
| `ProcessResult::ReadMore(n)` | a status enum, **not** an exception (it is the common path) |

Exception hierarchy mirroring `error.rs`:

```
DatabentoException                        (base)
├── DatabentoApiException                 RequestId, StatusCode, Case, Message, DocsUrl, Payload
├── DatabentoAuthenticationException
├── DbnException                          codec / decode failures
├── HeartbeatTimeoutException             no data within the expected interval
├── ConnectTimeoutException
└── AuthTimeoutException
```

### `ProcessResult<R>` (data-carrying enum) → status enum + out-params
C# `enum` cannot carry payloads and a class hierarchy would allocate on the hot path.

```csharp
public enum ProcessStatus { ReadMore, Metadata, Record, Error }

ProcessStatus Process(out int bytesNeeded);   // Record: read via LastRecord
```

Keep the metadata retrievable separately rather than boxing it into the return value — metadata
is decoded exactly once per session, records millions of times.

### `Symbols` (Rust sum type) → `readonly struct` + factories
No discriminated unions in C# yet. Model as a struct with a private discriminator:

```csharp
Symbols.All
Symbols.FromIds(1, 2, 3)
Symbols.From("ES.FUT", "CL.FUT")
implicit operator Symbols(string)      // the common single-symbol case
```

Port `to_chunked_api_string()` faithfully: **chunk at 500 symbols per message**, and only the
final chunk carries `is_last=1`.

### `SymbolIndex` + `Index<&R>` → `ISymbolIndex`, with no indexer  (#13)

Upstream pairs the `SymbolIndex` trait's `get_for_rec` with `std::ops::Index<&R>` impls that
`unwrap()` (`symbol_map.rs:342-364`), so a miss panics. The trait ports directly as
`ISymbolIndex`; the `Index` impls do not port at all.

A C# indexer carries the same throw-on-miss expectation `Dictionary<K,V>` sets, and a symbol-map
miss is *expected*, not exceptional — a live stream resolves nothing for an instrument until its
mapping record arrives, and a timeseries map holds nothing for a date outside the query's range.
An indexer would make the ordinary case throw, which is the `Result<T,E>` → `Try*` rule above
applied unchanged. The whole surface is `TryGetSymbol`.

Two details that are easy to get backwards:

- **`PitSymbolMap.TryGetSymbol(record)` does not read the record's timestamp**, and that is
  upstream's behavior, not an omission (`symbol_map.rs:336-340` is `self.get(record.header()
  .instrument_id)`). A point-in-time map was already resolved for one date. `TsSymbolMap` keys on
  the record's own index date; the asymmetry is the difference between the two types.
- **`Record::index_date()` does not reach every record type in .NET.** It is a default trait
  method upstream, so every `Record` gets it free. A C# default interface member cannot replace
  it — calling one on a record struct needs boxing or a generic constraint, and a generic
  extension method cannot take its receiver by `in` at all (CS8338; C# 14's extension blocks keep
  the restriction as CS9301), so either shape copies a 520-byte `InstrumentDefMsg` to read a date.
  So `IndexDate()` / `TryIndexDate()` are extension methods on `RecordRef` only, where the rtype
  dispatch is the thing worth hiding. For a concrete record struct the equivalent is already a
  one-liner through the same sentinel-checking crossing:
  `DbnTime.TryToUtcDate(def.IndexTs, out var date)`.

`IRecord<T>` exposes `IndexTs` but not the header, because every record declares its header as a
*field* named `Header` and a struct cannot have a field and an interface property of the same
name. The typed `TryGetSymbol<TRecord>` overload therefore reads the instrument ID by
reinterpreting the record at offset 0 as a `RecordHeader` — an invariant `RecordLayoutTests`
asserts for every record type, and one `WithTsOut<T>`'s constructor already depends on.

### `MockGateway` + `Fixture` → `MockLiveGateway`, with no actor  (#18)

Upstream's test module holds two types. `MockGateway` speaks the gateway half of the line
protocol; `Fixture` wraps it in a spawned task fed by an unbounded channel, with an `Event` enum
mirroring every gateway method. **Port the first, drop the second.** The channel exists so one
Rust test can drive both ends of a socket; in .NET a test starts the client's leg, awaits the
gateway's, and joins. Everything after the handshake is a write that completes without waiting
for a reply, so the rest of a session test reads as straight-line code.

Three further departures, each of which makes the double stricter or the ordering possible:

- **`assert!` → a `MockGatewayException` carrying the offending line.** The harness usually runs
  a step ahead of the assertion that will fail, so a bare assert surfaces as an unattributable
  failure several frames away. A named exception also lets the harness's *own* tests assert that
  it rejects a malformed client, which `ThrowsAnyAsync<Exception>` could not distinguish from a
  dead socket.
- **The CRAM response is verified, not just shape-checked.** Upstream asserts only that the
  digest is hex. The gateway knows the challenge and the key, so it can compute the digest —
  and a client that hashes `key|challenge` instead of `challenge|key` produces a perfectly
  well-formed digest that upstream's check waves through.
- **The expectation type is the harness's own** — `ExpectedSubscription`, not the client's
  `Subscription`. Upstream can pass its own type because the mock lives inside the crate it
  tests; here that would mean the harness could not exist until `DatabentoDotNet.Live` did,
  inverting #10's sequencing, and it would weaken the check by handing the expectation and the
  implementation the same object. `ExpectedSubscription.Symbols` is therefore **one chunk**,
  not the whole subscription — upstream compares against the un-chunked list, which is why its
  own chunking test cannot use `expect_subscribe` at all.

**The no-`System.IO.Pipelines` rule in §3 does not reach the gateway's line reads.** That rule is
about the client reinterpreting records in place; the gateway reads control lines and never a
byte of DBN, so it buffers freely. The *client* stub is the one that reads its control lines a
byte at a time, because the next thing it reads is binary — possibly a zstd frame — and an
over-read there cannot be given back.

### `tracing` → `ILogger` with source-generated messages
Use `Microsoft.Extensions.Logging`. On the per-record path (`log_record`), use
`[LoggerMessage]` source generators so disabled log levels cost no allocation. Do not
string-interpolate in the record loop.

### `time::OffsetDateTime` → NodaTime, split by layer
- **API parameters** (subscription `start`, historical ranges): `Instant` for a point on the
  timeline, `LocalDate` for a calendar date. Not `DateTimeOffset` — the BCL date and time types
  are banned repo-wide and the build fails on them (`BannedSymbols.txt`, RS0030). See CLAUDE.md,
  "Dates and times".
- **Record timestamps** (`ts_event`, `ts_recv`, `ts_out`): keep raw `ulong` nanoseconds. A record
  field's type *is* its wire layout, so this does not bend for API convenience.
- **The crossing between the two is `DbnTime`**, and it is the only one. Every conversion checks
  the `ulong.MaxValue` sentinel, because the obvious cast wraps it to −1 ns without throwing.
  Never convert implicitly.

Anywhere `TimeSpan` would have been reached for — timeouts, heartbeat intervals, retry backoff —
use `Duration`.

---

## 3. The one reflexive .NET choice that is *wrong* here

**Do not use `System.IO.Pipelines` for the live socket.**

It is the idiomatic answer for .NET socket parsing, and it is the wrong tool for this codec:

1. `PipeReader` yields `ReadOnlySequence<byte>`, which may be **non-contiguous**. Zero-copy
   record access needs a contiguous, correctly-aligned span to reinterpret via
   `MemoryMarshal.AsRef<T>`. Reassembling segments defeats the entire point.
2. Pipelines owns its own buffering, which would sit *on top of* the FSM's buffer — two layers
   of copying where the design calls for zero.
3. It cannot guarantee 8-byte alignment, which `MemoryMarshal.AsRef<T>` requires for correctness
   on platforms with strict alignment.

Use `NetworkStream.ReadAsync(Memory<byte>)` (or `Socket.ReceiveAsync`) writing directly into
`fsm.SpaceMemory()` — the .NET spelling of the Rust client's `self.fsm.space()`. See
"The read seam" in §1 for why it is `SpaceMemory()` and not `Space()`.

---

## 4. Behavioral details that are easy to miss

Each of these is a real behavior in the Rust client that a naive port would drop.

- **Heartbeat timeout is `heartbeat_interval + 5s`, defaulting to `35s`** when no interval is
  configured (`client.rs::heartbeat_timeout`). On timeout the client marks itself closed and
  raises — it does not silently retry.
- **`heartbeat_interval` must be 5–1800 seconds**, and sub-second precision is ignored with a
  warning.
- **`reconnect()` reuses the already-resolved `peer_addr`**, resets `sub_counter` to 0, resets
  the FSM, and re-authenticates. It does **not** re-resolve DNS.
- **`resubscribe()` clears each subscription's `start`.** Critical: without this, a reconnect
  would replay history a second time. It also restores `sub_counter` to the max existing id.
- **`reconnect` and `resubscribe` are deliberately separate.** Replaying subscriptions is the
  caller's decision; do not fuse them into an auto-reconnect.
- **Subscription ids auto-increment** from 1 when not supplied, warning (not failing) at
  `u32::MAX`.
- **`use_snapshot` conflicts with `start`** — rejected client-side before sending.
- **`use_snapshot` is only supported with the MBO schema.**
- **Auth and subscribe are NOT cancel-safe**; `next_record` and `fill_buf` are. A partially
  written control message desyncs the gateway and it closes the connection. In .NET: do not
  thread a `CancellationToken` into the middle of those writes — cancel by tearing down the
  socket.
- **API key: exactly 32 ASCII chars**; `bucket_id` is the **last 5**. Reject the literal
  `$YOUR_API_KEY` placeholder with a clear message.
- **Records may arrive as DBN v1/v2** and are upgraded in-flight per `VersionUpgradePolicy`
  (default `UpgradeToV3`). `log_record` shows the v1 fallback path for `SystemMsg`/`ErrorMsg`.
- **`SystemMsg` carries `SystemCode`** — `Heartbeat`, `EndOfInterval`, `SlowReaderWarning`.
  Heartbeats arrive as ordinary records, not control frames.
- **Schema wire strings are not `ToString().ToLower()`** — they are `mbp-1`, `ohlcv-1s`,
  `cbbo-1m`, etc. Map them explicitly.
- **Historical auth is HTTP Basic with the API key as username and an empty password.**
  Surface the `X-Warning` header; log `request-id` on every error.

---

## 5. Suggested port order

Follows `ROADMAP.md`, annotated with the source file for each step.

| Step | Port from | Notes |
|---|---|---|
| M1a enums | `dbn/src/enums.rs`, `publishers.rs` | Generate the publisher/venue tables; do not hand-write |
| M1b records | `dbn/src/record.rs` + `databento-cpp/include/databento/record.hpp` | C++ `static_assert`s are the size oracle |
| M1c metadata | `dbn/src/encode/dbn/sync.rs`, `metadata.rs` | Prelude 8 B, fixed section 100 B |
| M1d FSM + buffer | `dbn/src/decode/dbn/fsm.rs`, `aligned_buffer.rs` | Drop `State::Consume` |
| M1e symbol maps | `dbn/src/symbol_map.rs` | `SymbolIndex` ports; its `Index<&R>` impls do not — §2 |
| M2 live | `databento-rs/src/live/{protocol,client}.rs` + `live.rs` | Mock gateway first (#18), then connect (#19); §4 above is the checklist |
| M3 historical | `databento-rs/src/historical/*.rs` | `timeseries.get_range` reuses the M1 decoder |
| M4 reference | `databento-rs/src/reference/*.rs` | zstd-JSONL, **not** DBN |

The mock gateway upstream ships in `live/client.rs`'s test module is ported, not reinvented, and
it lands *before* the client rather than alongside it — see `MockLiveGateway` in
`tests/DatabentoDotNet.Live.Tests` and §2 above for what changed on the way across.
