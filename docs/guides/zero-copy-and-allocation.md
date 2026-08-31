# Zero-Copy and Allocation

**Records are reinterpreted in place over the read buffer — decoding one allocates nothing.**
That guarantee is the reason this library exists, and it is measured by a test rather than
claimed in a README.

The cost is a lifetime rule: a `RecordRef` is valid only until the next call on the decoder. This
page explains what the compiler enforces for you, what it cannot, and what to do when you need a
record to outlive its buffer.

---

## What "zero-copy" means here

```csharp
while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade)) { Handle(trade); }
}
```

No byte is copied between the socket and `record`. `TryNextRecord` finds a complete record in the
buffer and hands back a `ReadOnlySpan<byte>` over it; `Get<T>` reinterprets those same bytes as a
struct with `MemoryMarshal.AsRef<T>`. The struct's field layout *is* the wire layout — which is
also why record fields stay `ulong` and `long` rather than becoming `Instant` and `decimal`.

The buffer itself is a `ulong[]`, not a `byte[]`, so the bytes start 8-byte aligned no matter what
the runtime decides to do. Reinterpreting a misaligned buffer is undefined behaviour on some
architectures and merely slow on x86, which is the worst combination: it would pass every test on
a developer machine.

## The lifetime rule, and who enforces it

`RecordRef` is a `readonly ref struct`. That is not decoration — it is the enforcement mechanism.
The compiler will refuse to let you:

| You try to | Compiler says |
|---|---|
| Store it in a field or a `List<RecordRef>` | CS8345 — a `ref struct` cannot be a member |
| Hold it across an `await` | **CS4007** — cannot be in scope across an `await` |
| `yield return` it | CS4013 — cannot appear in an iterator |
| Box it, or use it as a generic argument | CS0029 / CS0306 |
| Capture it in a lambda or local function | CS4013 — cannot be used inside a nested function |
| Return it from a method outliving the buffer | CS8352 — may expose referenced variables |

Every one of those errors is the lifetime rule stated in a place a human cannot forget it. If your
code compiles, the record does not outlive its buffer.

**This is why there is no `Task<RecordRef>`, and there never can be.** An `async` method cannot
return a `ref struct`. Upstream's single-call `next_record()` therefore does not port; its
`fill_buf()` / `try_next_record()` pair does, which is why the live loop is two calls:

```csharp
while (true)
{
    while (client.TryNextRecord(out var record)) { /* synchronous — no await in here */ }

    if (await client.FillBufferAsync(ct) == 0) { break; }
}
```

A `RecordRef` *local* inside an `async` method is fine. Only one that survives an `await` is
rejected.

### What the compiler does not catch

One thing: the buffer's contents change on the next call, and `Bytes` is a span the compiler
believes is still valid within the same statement sequence. Do not do this:

```csharp
decoder.TryNextRecord(out var a);
decoder.TryNextRecord(out var b);
Compare(a, b);                       // compiles. `a` now points at whatever `b` displaced.
```

Two records at once means copying one of them out. Which brings us to the escape hatches.

## When a record has to outlive its buffer

### The cheapest: copy the struct

`TryGet<T>` copies the record into an ordinary `readonly struct`, and an ordinary struct has no
lifetime restrictions at all. It can be stored, awaited across, put in a list, and returned:

```csharp
var trades = new List<TradeMsg>();

while (decoder.TryNextRecord(out var record))
{
    if (record.TryGet(out TradeMsg trade))
    {
        trades.Add(trade);           // fine — `trade` is a value, not a view
    }
}
```

A `TradeMsg` is 48 bytes and the copy is a register-width move or two. When you know the record
type you want, this is almost always the right answer.

One caveat for records with text fields: `def.RawSymbol.AsSpan()` returns a span *into the struct
you are holding*. Copy the struct to a local, and the span points at that local — correct, but
only for as long as the local lives. Call `ToString()` if the text must outlive it.

### The general one: `OwnedRecord`

When you do not know the type, or want the whole record regardless:

```csharp
OwnedRecord owned = OwnedRecord.CopyOf(record);
// … much later, on another thread if you like …
if (owned.TryGet(out TradeMsg trade)) { /* … */ }
```

`OwnedRecord` holds its own `ulong[]` and exposes the same `Has` / `TryGet` / `Get` / `IndexTs`
surface as `RecordRef`, plus `AsRef()` to get a `RecordRef` back over its own storage. It costs
**two allocations** — the object and its array — around 110 bytes for a typical trade.

This is what `LiveClient.RecordsAsync()` yields, and why:

```csharp
await foreach (OwnedRecord record in client.RecordsAsync(ct)) { /* … */ }
```

`yield return` carries the same restriction `await` does, so a `ref struct` cannot leave an
iterator at all. The copy is necessary rather than a convenience that could be optimised away —
which is why `OwnedRecord` states the cost on its own type rather than hiding it behind the
pleasant `await foreach`.

## Choosing a loop

| | `FillBufferAsync` + `TryNextRecord` | `RecordsAsync` |
|---|---|---|
| Per record | **0 bytes** | ~110 bytes, 2 allocations |
| Shape | Nested `while`, manual drain | `await foreach` |
| Records may cross `await` | No | Yes |

`RecordsAsync` is written in terms of the other two and does not bypass them, so the surfaces
cannot diverge. Use it when records are individually interesting and the rate is modest; use the
zero-copy loop for full-depth MBO at market open, where the difference between 0 and 110 bytes per
record is the difference between a steady heap and a GC pause in the middle of the session.

## What the tests actually assert

Two files — `AllocationTests` (codec) and `LiveAllocationTests` (over the mock gateway's socket) —
measure `GC.GetAllocatedBytesForCurrentThread()` around a steady-state loop and require **exactly
zero**. Not "small". Zero.

The codec test runs over the whole vendored corpus, so a regression on any record type is caught,
not just on the one the test happened to pick.

Both files also contain a test that the *measurement itself* notices a deliberate allocation on
the same path. A broken instrument reporting zero would pass every other assertion in them, so the
instrument is verified too.

Anything added to the `FillBufferAsync` / `TryNextRecord` path has to keep those green. The
benchmark project reports the same numbers but enforces nothing — a benchmark someone has to
remember to run cannot hold a guarantee.

### What the zero does and does not cover

It is a **per-record** guarantee. Decoding records from bytes already buffered allocates nothing,
and that is what the assertion pins.

A call to `FillBufferAsync` that actually suspends — a quiet feed waiting on the next heartbeat —
has the ordinary per-call cost of an async socket read: a state machine box, the timeout
machinery, the cancellation registration. That is per *fill*, not per record, so it is amortised
across every record the fill delivers, and on a busy stream where reads are satisfied from bytes
the kernel already holds it does not arise at all. The live allocation test is deliberately sized
so that every measured read is of that second kind, which is both the case worth measuring and the
one where nothing suspends.

If you are counting bytes at that level, run the benchmarks:

```sh
dotnet run -c Release --framework net10.0 \
  --project benchmarks/DatabentoDotNet.Benchmarks -- --filter '*'
```

Release only — BenchmarkDotNet refuses a Debug build.

## Things that do allocate, and are meant to

- `CStr71.ToString()` and friends — the reason `AsTextSpan()` exists. Never called during decoding.
- `PitSymbolMap.OnRecord` — exactly one `string` per genuinely new mapping, and nothing else.
- `TsSymbolMap.FromMetadata` — one dictionary entry per instrument-day, built once.
- The whole control plane: connecting, the CRAM handshake, subscription lines. Once per session,
  or once per subscription. `string.Join` and a `StringBuilder` there cost nothing that matters.

## Why not `System.IO.Pipelines`

It is the reflexive .NET answer for a socket read loop and it is wrong here, for two reasons.

`ReadOnlySequence<byte>` may be non-contiguous, and `MemoryMarshal.AsRef<T>` needs contiguous
bytes — a record split across two segments would have to be copied to be read, which is exactly
the copy this library is built to avoid. And it adds a second buffering layer over the state
machine's own, which already tracks where the complete records are.

Async reads go through `DbnFsm.SpaceMemory()` instead, a `Memory<byte>` projected over the
buffer's `ulong[]` by a `MemoryManager<byte>` — there is no `ReadAsync(Span<byte>)` and no
`Memory<T>` reinterpret cast, so the projection is the seam. `Space()` is derived from
`SpaceMemory()` so the two views cannot drift.

The alternatives that were considered and rejected are in
[`PORTING.md` §1 and §3](https://github.com/jerbersoft/databentodotnet/blob/master/PORTING.md).

## See also

- [Live Streaming](live-streaming.md) — the two loops in context
- [Decoding DBN Files](decoding-dbn-files.md) — `Has` / `TryGet` / `Get` and the version-length rule
- [Troubleshooting](troubleshooting.md) — the compiler errors above, with fixes
