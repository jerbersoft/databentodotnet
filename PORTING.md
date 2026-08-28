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
`fill_buf` / `try_next_record` pair does, and it is what `DatabentoDotNet.Live` exposes. (An
`IAsyncEnumerable<T>` of *copied* records is the ergonomic alternative for callers who do not
need zero-copy; `yield` has the same restriction as `await`, which is why it must copy.)

#### Two departures the port had to make  (#22)

**`FillBufferAsync` splits in half, and it is not a micro-optimisation.** The obvious shape —
build a linked `CancellationTokenSource`, register a socket-teardown callback, `await` the read —
costs three allocations on every call: the source, the registration, and the boxed `async` state
machine. Nothing about that is visible in the source, and on a stream where each read yields one
record it *is* a per-record allocation, which puts M2's definition of done out of reach. So the
read is started before any timeout machinery exists, and when it completes synchronously — the
ordinary case on a stream with bytes waiting — the method returns without building any of it. The
read budget therefore applies only to a read that actually waits, which is the only read it was
ever describing. `LiveAllocationTests` is what turned the original shape's cost from an opinion
into 72 bytes, and #28 is why that test exists at all.

**Cancellation ends the session, where upstream's `fill_buf` is cancel-safe.** Upstream can drop
a pending read inside a `tokio::select!` and lose nothing, because tokio's `AsyncRead` guarantees
a cancelled read consumed nothing. .NET makes no such guarantee about a socket read, and bytes
taken off the socket but not handed to the state machine are not a lost read — they are a decoder
that silently resumes mid-record. So a cancelled fill marks the client closed. The repair that
would restore true cancel-safety — race the read against the token and keep the pending `Task`
for the next call — was rejected: the buffer that read is writing into belongs to the state
machine, and the next `SpaceMemory()` may shift it underneath an in-flight read. That trades a
detectable failure for a data race.

The same asymmetry as the handshake, then, but for a different reason: auth and subscribe cannot
be cancelled safely because a half-written *write* desynchronises the gateway; `fill_buf` cannot
because a half-consumed *read* desynchronises us.

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

The snippet above is #19's shape. `Compression` and `AuthTimeout` arrived with the auth line
that carries them (#20), and `Subscriptions` with #21.

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

### `Protocol::authenticate` → `LiveClient.AuthenticateAsync`, with a budget  (#20)

Upstream puts the line protocol in its own `Protocol<W>` type and exposes it as a lower-level API.
That indirection exists because `authenticate` takes the reader and the writer as two separate
generic parameters — Rust cannot hand one `&mut TcpStream` to both halves at once. .NET has no
such constraint: one `NetworkStream` is both, so the handshake lives on the client and the only
piece that needed a type of its own is `Internal/ControlChannel`, which owns the `\n`-terminated
line format the whole session is configured over.

**Control lines are read one byte at a time.** The next thing on the socket after `start_session`
is DBN — possibly inside a zstd frame — so a buffered reader that pulled eight bytes too many
while reading the auth response would swallow the front of the metadata with no way to hand it
back. Three short lines per session make the syscalls irrelevant, and the alternative is a
hand-off from one buffer into the decoder's own on a path nothing routinely exercises.
`MockLiveGateway`'s reader buffers freely, because it never reads anything but lines.

**Cancellation tears the socket down; it is not threaded into the writes.** §4 records that auth
and subscribe are not cancel-safe, and this is what that means in .NET. `AuthenticateAsync`
registers a callback on its linked token that disposes the `Socket`, then passes
`CancellationToken.None` to every read and write underneath. A pending operation fails outright
rather than returning with half an auth line on the wire, and the caller is left disconnected —
which is the honest state, because a gateway that saw half a control message has already closed
its end. The registration is disposed *before* the success path assigns `IsAuthenticated`, so a
budget that elapses in the gap cannot leave a client that believes it is authenticated on a socket
it has just destroyed.

**Upstream has no auth budget at all**, so a gateway that accepts a connection and then says
nothing hangs `authenticate` until the OS gives up on the socket. `AuthTimeout` covers the whole
exchange rather than each line, since a gateway that stalls after the greeting has spent the
caller's time just as surely as one that never speaks.

**A malformed challenge is not an authentication failure.** Upstream's `Challenge::parse` returns
`Error::internal`, and the distinction is worth keeping: `strip_prefix` on a line without `cram=`
yields no challenge, and hashing an empty one produces a digest the gateway duly rejects — so
folding the two together reports a broken gateway as a bad API key and sends the caller off to
rotate credentials that were never at fault. Hence `LiveProtocolException` alongside
`DatabentoAuthenticationException`.

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

Split by module rather than mirroring `error.rs`'s single enum, so a caller can catch the live
client's failures without also catching an HTTP error from a historical query it never made:

```
DbnException                              codec / decode failures        (M1)

LiveException                             (live base)                    (#19, #20, #22)
├── LiveConnectException                  EndPoint — refused, unreachable, unresolvable
│   └── ConnectTimeoutException           Timeout — the attempt outlived the connect budget
├── LiveProtocolException                 the gateway said something the protocol does not allow
├── DatabentoAuthenticationException      Error, Response — the credentials were refused
├── AuthTimeoutException                  Timeout — the handshake outlived its budget
└── HeartbeatTimeoutException             Timeout — no data within the expected interval  (#22)

DatabentoApiException                     RequestId, StatusCode, Case, DocsUrl, Payload  (M3)
```

### `ProcessResult<R>` (data-carrying enum) → status enum + out-params
C# `enum` cannot carry payloads and a class hierarchy would allocate on the hot path.

```csharp
public enum ProcessStatus { ReadMore, Metadata, Record, Error }

ProcessStatus Process(out int bytesNeeded);   // Record: read via LastRecord
```

Keep the metadata retrievable separately rather than boxing it into the return value — metadata
is decoded exactly once per session, records millions of times.

### `Symbols` (Rust sum type) → `readonly struct` + factories  (#21)
No discriminated unions in C# yet, so the three arms become a struct with a `SymbolsKind`
discriminator and static factories:

```csharp
Symbols.All                                 // ALL_SYMBOLS
Symbols.From("ES.FUT")                      // one symbol
Symbols.From(["ES.FUT", "CL.FUT"])          // several, order preserved
Symbols.FromIds(101)                        // one instrument id
Symbols.FromIds([101u, 202u])               // several
```

`to_chunked_api_string()` ports faithfully: **chunk at 500 symbols per message**, only the final
chunk carrying `is_last=1`.

**No `implicit operator Symbols(string)`**, which an earlier draft of this section proposed.
Symbols are validated when the set is built — see below — so the conversion could throw, and an
implicit conversion that throws puts the exception on an assignment (`Symbols = userInput`)
where no reader expects one. `Symbols.From(userInput)` puts it where they do. Nothing is lost:
the single-symbol case is one extra call, and it is the same call for every other case.

**`params` overloads were not added either.** With collection expressions,
`Symbols.From(["A", "B"])` reads as well as `Symbols.From("A", "B")` and does not introduce an
overload pair that a collection-expression argument has to choose between.

**Two departures from upstream, both about failures upstream does not have:**

- **An empty symbol list is rejected at construction.** Upstream's `subscribe` computes
  `symbol_chunks.len() - 1` to find the last chunk; `chunks(500)` of an empty vector yields no
  chunks, so that subtraction underflows — a panic in debug, an enormous index in release. There
  is no meaningful empty subscription, so the set cannot be built.
- **A symbol carrying `,`, `|`, `=`, `\n` or `\r` is rejected at construction.** The
  subscription line separates fields with `|`, keys from values with `=`, symbols with `,`, and
  messages with a newline. A symbol containing one of those does not produce a *rejected*
  subscription — it produces a **different, well-formed** one, silently. That is precisely the
  class of failure this codebase exists to convert back into an exception, and construction is
  the earliest point at which the offending symbol is still in the caller's hand.

### `Client::subscribe` → `LiveClient.SubscribeAsync`, immutably  (#21)

Upstream takes ownership of the `Subscription`, assigns `id` in place if absent, and pushes the
mutated value onto `self.subscriptions`; `resubscribe()` later clears each stored `start` in
place. Neither is expressible on a C# record, so:

```csharp
Task<Subscription> SubscribeAsync(Subscription subscription, CancellationToken ct = default)
IReadOnlyList<Subscription> Subscriptions { get; }
```

`SubscribeAsync` **returns the subscription it sent**, with `Id` filled in, and appends that to
`Subscriptions`. A caller who wants the assigned id has it directly rather than having to read it
back out of a list. `Subscriptions` is read-only where upstream also exposes
`subscriptions_mut()`: the one thing that mutability is for upstream — clearing `start` before a
resubscribe, so a reconnect does not replay the same history twice — belongs to the resubscribe
itself (#23), not to callers.

**Ids stop rather than repeat.** Upstream warns at `u32::MAX` and then keeps handing out the same
id, so two subscriptions share one and the gateway's errors about them become unattributable.
This client has no logger to warn through, and a silently duplicated id is the
confidently-wrong outcome rather than the safe one, so `SubscribeAsync` throws. A caller who
genuinely needs more can set `Subscription.Id` themselves.

**Validation runs before the connection checks**, so a subscription this client would never send
is rejected identically whether or not a socket happens to be open — the caller's bug does not
depend on their timing. Both cross-property rejections live in `Subscription.Validate` rather
than in init accessors: an init accessor sees only its own value and whatever was set before it,
so the same object would be accepted or rejected depending on the order the initializer listed
its properties.

### `Client::reconnect` + `resubscribe` → `ReconnectAsync` + `ResubscribeAsync`  (#23)

```csharp
Task ReconnectAsync(CancellationToken ct = default)      // close, connect to Endpoint, handshake
Task ResubscribeAsync(CancellationToken ct = default)    // replay Subscriptions, each start dropped
```

Both port directly, and they stay **two calls**: replaying subscriptions is the caller's decision,
and fusing them into an auto-reconnect would replay subscriptions a caller may no longer want.
`ReconnectAsync` does not start the session either — that stays a third call, `StartAsync`, which
is also where billing begins.

`ResubscribeAsync` replaces each retained `Subscription` with `sub with { Start = null }` before
sending it, where upstream mutates its stored value in place. Same effect and the same reason:
`Subscriptions` reports what was last sent, and an entry that kept its `start` would replay the
same history again on the *next* reconnect.

**Two departures, both about state upstream resets and this does not.**

`reconnect()` sets `sub_counter = 0`, and `resubscribe()` then raises it back to the highest id it
replayed. In the ordinary reconnect-then-resubscribe sequence the two designs agree exactly. They
differ only when a caller reconnects and subscribes to something *new* without resubscribing:
upstream hands out id 1 again while its retained list still holds a different subscription with
that id, so `subscriptions()` carries two entries the gateway cannot tell apart in an error about
either. This counter is monotonic instead. Nothing on the wire notices — the id is a correlation
handle, not a sequence the gateway checks — and the duplicate pair becomes unrepresentable.
`ResubscribeAsync` still raises the counter past the ids it replayed, which is what covers an id a
*caller* chose.

`reconnect()` also calls `fsm.reset()` to reuse the decoder's buffer. `StartAsync` builds a fresh
`DbnFsm` per session instead. A reconnect is rare and the buffer is a one-time 64 KiB, so nothing
measured by #28 is affected; carrying a state machine across two sessions to save it would be the
more surprising of the two.

**One thing reusing the resolved address costs, which upstream does not pay.** A connect by host
name goes out on a dual-stack socket, so `RemoteEndPoint` — and therefore `Endpoint` — comes back
as an **IPv4-mapped IPv6 address** whenever the gateway answered over IPv4. `ReconnectAsync` dials
that address on a socket built for its family, and a `Socket(AddressFamily.InterNetworkV6, …)` is
`V6ONLY` by default: it refuses a mapped address outright. Since `LiveGateway.For` returns a
`DnsEndPoint`, that is every client that does not override `Gateway` — so `ConnectCoreAsync` sets
`DualMode` on any IPv6 socket it builds. Rust reaches `peer_addr` as a `SocketAddr` from a
resolution it performed itself and never sees the mapped form.

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

### `historical::Client` + `ClientBuilder<AK>` + four subclients → `HistoricalClient`  (#35)

Upstream's historical half is four things: a `Client` holding the key, base URL, gateway and
upgrade policy (`historical/client.rs:31-37`); a type-state `ClientBuilder<AK>` (`:259`) whose
`build()` exists only on `impl ClientBuilder<ApiKey>` (`:368-373`); four subclients —
`batch()`, `metadata()`, `symbology()`, `timeseries()`, each taking `&mut self` (`:102-118`); and
the transport underneath them — `pub(crate)` `get`, `get_with_path` and `post` over a private
`request` (`:125-154`), plus `check_warnings`, `check_http_error` and the two response handlers.

**The builder goes, for the same reason `ClientBuilder<AK, D>` does above.** `required` init
properties say "no client without a key" natively, and `HistoricalClient` carries the settings
itself rather than behind an options record: `ApiKey` (`required`), `Gateway`, `UpgradePolicy`,
`BaseUrl`, `UserAgentExtension`, `LoggerFactory`. One consequence worth knowing before reading the
constructor: an `init` accessor runs *after* the constructor body, so the `HttpClient` and its
`Authorization` header cannot be built there — both are `Lazy` with
`LazyThreadSafetyMode.ExecutionAndPublication`, which is what makes `required` init properties and
a fully configured `HttpClient` compatible at all.

**The four subclients stay — as facades, and #35 declares none of them.** `&mut self` is the
borrow checker's reason for them, but the shape it produces is the one upstream's own doc comment
presents as the way individual API methods are reached (`historical/client.rs:25-29`), so it ports
for its own sake rather than for Rust's. Note that this is *not* universal across Databento's
clients: databento-cpp puts every endpoint on one `Historical` class
(`include/databento/historical.hpp:26`) with the group as a method-name prefix —
`MetadataListDatasets`, `BatchSubmitJob` — which is the alternative being rejected here. A facade
with no endpoints on it is a public empty class, so each of #36–#39 declares its own alongside the
first endpoint that goes on it.

**The transport is `public` where upstream's is `pub(crate)`.** `SendAsync`, `ReadJsonAsync`,
`ReadZstdJsonLinesAsync` and the two composed forms `SendJsonAsync` / `SendZstdJsonLinesAsync` are
what the facades are built from, and they are also the escape hatch for an endpoint this library
has not wrapped yet. This repo declares no `InternalsVisibleTo`, so "internal but tested" is not a
shape available here, and #35's own definition of done requires driving an arbitrary slug through
a public method.

**`parameters` travel by HTTP method, not by endpoint.** Upstream has two families, `AddToQuery`
and `AddToForm` (`historical.rs:338-344`), and which one an endpoint uses is decided entirely by
its method: `add_to_query` is reached only from `GET`s (`metadata.rs:47`, `:129`) and every
`add_to_form` call site sits under a `post(…)`. So `SendAsync` takes one `parameters` argument,
sends it as an `application/x-www-form-urlencoded` body on `POST` and as a query string on
anything else — one rule, and no per-endpoint table for a future endpoint to be missing from. A
`null` or empty `parameters` on a `POST` is an *empty form*, not an absent body: the absent one
carries no `Content-Type`, and a server that branches on it sees a different request. Values go
through `Uri.EscapeDataString`, which escapes the comma a `Symbols` list renders — a sub-delimiter
a server splitting on raw ones would read as a differently shaped request rather than a rejected
one.

**`check_http_error` → `DatabentoApiException`**, per the `Result<T, E>` rule above: an API
rejecting a request is exceptional. Upstream's `ApiError` (`error.rs:63-79`) is one arm of the
single `Error` enum this port splits by module; the message is composed from the same parts in the
same order as upstream's `Display` (`error.rs:104-125`), with one departure. The status renders as
`{(int)statusCode} {statusCode}`, not upstream's `400 Bad Request`: `reqwest::StatusCode` has a
canonical reason-phrase table and `System.Net.HttpStatusCode` has none, and the BCL's own
`ToString()` is not a clean fallback because it is not consistent — `BadRequest` for a named
member, `498` for one the enum does not name. Pulling in a reason-phrase table (ASP.NET Core's
`ReasonPhrases`, say) to reproduce upstream's exact text was rejected: a dependency on a shipping
HTTP *client* library for one string.

**`check_warnings` → `ILogger`, and not a property on any response** — see the `tracing` entry
below for the rule, and ROADMAP.md §5 for why the response wrapper lost.

**`handle_zstd_jsonl_response` ports here and nothing in M3 calls it.** Upstream defines it in
`historical/client.rs:212-229` and calls it only from `src/reference/`, which is M4. It is placed
where upstream places it rather than moved; ROADMAP.md §5 names the four call sites.

### `tracing` → `ILogger` with source-generated messages
Use `Microsoft.Extensions.Logging`. On the per-record path (`log_record`), use
`[LoggerMessage]` source generators so disabled log levels cost no allocation. Do not
string-interpolate in the record loop.

**Not every upstream `tracing` site ports, and the rule that decides is: this library logs only
what the caller cannot otherwise see.** Two of the three messages in
`DatabentoDotNet.Historical/Internal/HistoricalLog.cs` sit where the exception is *swallowed* — a
malformed `X-Warning` header, because the request deliberately carries on without it, and an
unparseable error body, because the exception describing it is replaced by a
`DatabentoApiException` carrying the body verbatim. In both, the log line is the only surviving
record.

`ServerWarning` is the third and is not one of those: no exception is involved on its path at all,
since it is called with a parsed string on the success side of that `catch`. It qualifies under the
governing rule instead — a caller who reaches the API through `SendJsonAsync` never holds the
`HttpResponseMessage` and so can never read the header themselves. **Do not narrow the rule to the
swallowed-exception case**: that would rule out the one message [#35]'s definition of done is about.

Two further upstream sites are therefore deliberately **not** ported. `deserialize_json`
(`historical/client.rs:231-236`) logs a JSON decode failure; the per-line `error!` in
`handle_zstd_jsonl_response` (`:224`) is *not* a JSON log despite sitting on that path — it fires
when `next_line()` fails, a zstd or IO failure, the JSON decode there being `deserialize_json` at
`:226`. Both sit where this port *throws* instead, so what fails reaches the caller as the
exception it was and a log line would only duplicate what they already hold.

Which exception, exactly, is worth stating rather than assuming — guessing it wrong is how the
filter on `CreateApiExceptionAsync`'s catch nearly ended up naming one type. A JSON decode failure
arrives as a `JsonException` carrying `Path`, `LineNumber` and `BytePositionInLine`; upstream's
`crate::Error::from(err)` keeps the `serde_json::Error` whole through its `#[from]`, so it carries
the equivalent `line` and `column` and the .NET one adds `Path` on top. A failed *read* arrives as
an `IOException` **or** as the `HttpRequestException` that wraps one — the measured note at that
catch is the record. A failed *decompression* is neither: a corrupt frame throws
`ZstdSharp.ZstdException`, which derives straight from `Exception`, and a frame that merely ends
early throws `EndOfStreamException`, which is an `IOException`. Measured, all three.

And `deserialize_json` interpolates `?str`, which for `handle_response` is the *entire response body*:
unbounded in size, and market data belonging to the caller's customers written into their logs at
`error` level by a library they never configured for it. A reader arriving from an unlogged
exception on either path is looking at a decision, not an oversight.

Event ids are stable identifiers. A caller can filter on one, so adding a message means adding an
id, never renumbering or reusing one.

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

### metadata::MetadataClient + GetQueryParams + 3 type aliases → MetadataClient  (#36)

Nine things a reader porting the next endpoint group (#37 `symbology`, #38 `timeseries`, #39
`batch`) needs and cannot get from reading the Rust source alone. The last two are the most
valuable, because they are pure discovery rather than a mapping choice.

1. **The slug carries the group prefix.** `metadata.{endpoint}` is built by the facade's own
   `get`/`post` helpers (`metadata.rs:196-202`) before the transport prepends `v0/`. #37–#39 build
   theirs the same way, with `symbology.`, `timeseries.` and `batch.`.
2. **Upstream's three type aliases collapse to one type.** `GetRecordCountParams`,
   `GetBillableSizeParams` and `GetCostParams` are all `GetQueryParams` (`metadata.rs:348-359`). A
   C# `using` alias is file-scoped and would buy nothing that a single shared type doesn't already
   have — see ROADMAP.md §5 for why the sharing itself, not just the collapsing, is the point.
3. **The two response enums carry no serde attribute upstream.** `FeedMode` and `DatasetCondition`
   get their wire spellings from a hand-written `FromStr` plus a `Deserialize` that goes through it
   (`metadata.rs:361-439` — the block starts at the `AsRef<str>` impl for `FeedMode`, fifty lines
   below the enum declaration itself, not at `:370`). Reading the enum declaration alone tells you
   nothing. Both are read-only on the wire: upstream gives them no `Serialize` at all.
4. **`System.Text.Json` matches the C# member name for an enum dictionary key, not the wire
   string.** Two `metadata.*` responses are keyed by schema — `list_unit_prices`' `unit_prices`
   (`metadata.rs:274`) and `get_dataset_range`'s `schema` (`metadata.rs:317`) — and the built-in
   enum-key handling reads `{"Ohlcv1S":…}`, which the API never sends, while rejecting
   `{"ohlcv-1s":…}`, which it does. **A test written from the C# names passes against a converter
   that does not work.** One `JsonConverter<Schema>` overriding `ReadAsPropertyName` covers the
   value position, both keyed dictionaries, and the unknown-name error in either position —
   `SchemaJsonConverter` is that one converter.
5. **The naming policy is `SnakeCaseLower`,** and the only two properties it cannot reach are
   `FieldDetail.TypeName` → `"type"` (`type` is a C# keyword) and `DatasetRange.RangeBySchema` →
   `"schema"` (the wire name says nothing about the map underneath it). Both carry an explicit
   `[JsonPropertyName]`.
6. **`deserialize_date_time` needs six NodaTime patterns where `time` needs two**
   (`databento-rs/src/deserialize.rs:7-19`). `InstantPattern.ExtendedIso` parses a zoned value and
   throws on a zone-less one; `LocalDateTimePattern.ExtendedIso` does exactly the reverse, so the
   ISO branch alone needs both. NodaTime has no optional-section syntax, so the legacy branch's
   four combinations of "subsecond or not" and "offset or not" each need their own pattern — measured
   against the actual accepted shapes, not reasoned from the format string. Our six are a superset
   of what upstream accepts by roughly two shapes, all in the accepting direction, with one
   exception in the other direction: ISO-8601 with a numeric offset (`2023-06-14T10:00:00+05:00`)
   is the one input upstream accepts and this converter rejects. Upstream's ISO branch parses into
   a zone-less `PrimitiveDateTime` and silently discards any offset it read, so rejecting that
   input outright is the better behaviour — but it is a real, deliberate divergence, not an
   oversight.
7. **`DateTimeRange` is now read as well as written.** #33 built it to render onto a request;
   `get_dataset_range` nests one per schema (`metadata.rs:317`), spelled as ISO timestamps on the
   way in and Unix nanoseconds on the way out — the same type crossing the wire in both directions,
   through two different converters.
8. **Converters public, serializer context internal.** With no `InternalsVisibleTo` anywhere,
   "internal but tested" is not a shape this repo has (`HistoricalClient.cs:64-66`), so anything
   needing a direct unit test has to be public. The six converters in
   `DatabentoDotNet.Historical.Json` need them — the six-pattern `Instant` reader most of all — so
   they are public. `MetadataJson`, the source-generated context, does not: it is exercised through
   every endpoint that uses it, which is a better test of a serializer context's configuration than
   reading its attributes back would be. #37–#39 should split theirs the same way.
9. **Two nested `JsonSerializerContext` classes with the same simple name crash the source
   generator, even when they sit in different outer types.** `System.Text.Json`'s generator names
   its emitted file `{ContextSimpleName}.{TypeName}.g.cs`, keyed on the context class's simple name
   alone — the containing type and the namespace play no part. `MetadataResponseTests`' fixture
   context was originally nested as `Json`, the same simple name `MetadataJsonConverterTests` a few
   files over already used for its own; both declare `decimal` in their `[JsonSerializable]` list,
   and the two collided on `Json.Decimal.g.cs` with CS8785 ("must be unique within a generator")
   even though the two classes are nested in unrelated outer types. That is a generator crash, not
   a name-resolution error, so nothing about the message points at a same-named class in a
   different file as the cause. Renaming one to `ResponseJson` was the whole fix, confirmed by
   making exactly that change and nothing else in a clean build — reproduced, not inferred. Simple
   names already taken in `tests/DatabentoDotNet.Historical.Tests` today: `TestJson`
   (`HistoricalClientTests.cs`), `Json` (`MetadataJsonConverterTests.cs`), `ResponseJson`
   (`MetadataResponseTests.cs`). #37–#39 need names outside that set.

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
  raises — it does not silently retry. *(#22 — `LiveClient.EffectiveReadTimeout`, with
  `ReadTimeout` as an override upstream does not offer, raising `HeartbeatTimeoutException`. The
  upstream name is kept deliberately: the timeout is only *justified* by the gateway's promise to
  send a heartbeat when nothing else is due, so calling it a read timeout would name the mechanism
  and lose the reason. Landed here rather than in #23 because the liveness check belongs in the
  read loop, as #23's own porting notes say.)*
- **`heartbeat_interval` must be 5–1800 seconds**, and sub-second precision is ignored with a
  warning.
- **`reconnect()` reuses the already-resolved `peer_addr`**, resets `sub_counter` to 0, resets
  the FSM, and re-authenticates. It does **not** re-resolve DNS. *(#23 — `ReconnectAsync` takes
  `Endpoint` explicitly and consults neither `Gateway` nor `Dataset`. The counter and the FSM are
  the two departures; §2 has the argument for both, and the mapped-address hazard that reusing a
  resolved address introduces in .NET and does not in Rust.)*
- **`resubscribe()` clears each subscription's `start`.** Critical: without this, a reconnect
  would replay history a second time. It also restores `sub_counter` to the max existing id.
  *(#23 — and the retained `Subscriptions` entry is replaced, not just the line on the wire, so a
  second reconnect cannot replay a start the first one already dropped.)*
- **`reconnect` and `resubscribe` are deliberately separate.** Replaying subscriptions is the
  caller's decision; do not fuse them into an auto-reconnect. *(#23 — and `StartAsync` stays a
  third call, since it is the one that begins billing.)*
- **Subscription ids auto-increment** from 1 when not supplied, warning (not failing) at
  `u32::MAX`. *(#21 — and it fails rather than warns; see §2.)*
- **`use_snapshot` conflicts with `start`** — rejected client-side before sending. *(#21)*
- **`use_snapshot` is only supported with the MBO schema.** *(#21 — rejected client-side too,
  where upstream documents it and leaves enforcement to the gateway. Discovering it there costs
  a round trip and a closed connection, and the answer was knowable before the socket was
  written to. Note that no dataset this account is licensed for offers MBO at all, which is why M2 measures
  allocation against the mock replaying synthetic MBO — ROADMAP.md §4, via #27.)*
- **Auth and subscribe are NOT cancel-safe**; `next_record` and `fill_buf` are. A partially
  written control message desyncs the gateway and it closes the connection. In .NET: do not
  thread a `CancellationToken` into the middle of those writes — cancel by tearing down the
  socket. *(#22 — and `FillBufferAsync` is not cancel-safe either, for the opposite reason: a
  half-consumed read desynchronises the decoder where a half-written line desynchronises the
  gateway. §1 has the argument and the repair that was rejected.)*
- **API key: exactly 32 ASCII chars**; `bucket_id` is the **last 5**. Reject the literal
  `$YOUR_API_KEY` placeholder with a clear message.
- **Records may arrive as DBN v1/v2** and are upgraded in-flight per `VersionUpgradePolicy`
  (default `UpgradeToV3`). `log_record` shows the v1 fallback path for `SystemMsg`/`ErrorMsg`.
- **`SystemMsg` carries `SystemCode`** — `Heartbeat`, `EndOfInterval`, `SlowReaderWarning`.
  Heartbeats arrive as ordinary records, not control frames. *(#23 — asserted by replaying one
  through the mock gateway between two MBO records and requiring the stream to stay in step
  afterwards, which is what a client that expected a separate control channel would break.)*
- **Records the gateway *generates* carry `publisher_id = 0`, which is not a valid `Publisher`.**
  `Publisher` starts at 1, so there is deliberately no name for zero, and any code that converts a
  header's publisher without checking throws on the first heartbeat. Upstream builds all three the
  same way — `ErrorMsg::new` and `SystemMsg::heartbeat` as
  `RecordHeader::new(rtype, 0, 0, ts_event)`, and `SymbolMappingMsg::new` as
  `RecordHeader::new(rtype::SYMBOL_MAPPING, 0, instrument_id, ts_event)`. Note the last: **no
  publisher, but a real instrument**, since naming an instrument is the point of the record.
  *(#29 — found by running the real-gateway lifecycle test for the first time, which failed on a
  heartbeat and then, once heartbeats were exempted, on the symbol mappings the gateway sends at
  the head of every session. The mock could not have caught either: it replays what we tell it to.)*
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
| M2 live | `databento-rs/src/live/{protocol,client}.rs` + `live.rs` | Mock gateway first (#18), then connect (#19), the handshake (#20), then subscriptions (#21); §4 above is the checklist |
| M3 historical | `databento-rs/src/historical/*.rs` | `timeseries.get_range` reuses the M1 decoder |
| M4 reference | `databento-rs/src/reference/*.rs` | zstd-JSONL, **not** DBN |

The mock gateway upstream ships in `live/client.rs`'s test module is ported, not reinvented, and
it lands *before* the client rather than alongside it — see `MockLiveGateway` in
`tests/DatabentoDotNet.Live.Tests` and §2 above for what changed on the way across.
