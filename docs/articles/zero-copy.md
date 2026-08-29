# The zero-copy contract

**A `RecordRef` points into the decoder's read buffer. It is valid until the next call on that
decoder, and not one call longer.**

That sentence is the whole contract, and this page exists because breaking it does not throw. The
bytes a stale `RecordRef` reads are still mapped, still aligned, and still a well-formed record —
they are simply a *different* record than the one you thought you were holding. A price comes out
plausible and wrong. Every other rule below follows from wanting that failure to be impossible
rather than merely unlikely.

## Why the library is built this way

Market data arrives in volume. A `trades` subscription on a liquid US equities dataset delivers
millions of records in a session, and the conventional .NET shape — allocate an object per record,
hand it to the caller, let the GC collect it — puts that allocation on the hot path of every one of
them. The cost is not the allocation itself so much as what it does to the collector at that rate.

So records are not allocated. `DbnDecoder` reads into a buffer it owns and reuses, and
<xref:DatabentoDotNet.Dbn.RecordRef> is a **`ref struct`** that reinterprets a span of that buffer
in place. `RecordRef.Get<T>()` is a bounds check and a cast, not a copy.

This is measured rather than asserted: `AllocationTests` and `LiveAllocationTests` wrap
`GC.GetAllocatedBytesForCurrentThread()` around a steady-state loop and require the answer to be
**exactly zero** — over the whole vendored fixture corpus, and over a live session against the mock
gateway. Both files also contain a test proving the *measurement* notices a deliberate allocation,
because an instrument that always reported zero would pass every other assertion in them.

## What the compiler enforces for you

`ref struct` is not a naming convention; it is a type-system constraint. The compiler will not let
a `RecordRef`:

- be stored in a field of a class or a normal struct,
- be boxed, or captured in a lambda or a closure,
- be placed in an array, a `List<T>`, or any generic collection,
- **survive an `await`** — the compiler rejects it with `CS4007`.

That last one is the important one, and it is why this library's async surface looks the way it
does. There is no `Task<RecordRef>` and there never can be: an `async` method cannot return a ref
struct. Upstream's Rust `LiveClient::next_record()` therefore does not port. What ports is its
`fill_buf()` / `try_next_record()` pair:

```csharp
while (true)
{
    // Drain everything the last read already produced. Synchronous, so no await
    // can happen while a RecordRef is alive.
    while (client.TryNextRecord(out var record))
    {
        Handle(record);
    }

    // Then, and only then, go back to the socket.
    if (await client.FillBufferAsync(cancellationToken) == 0)
    {
        break;   // gateway closed the stream
    }
}
```

Drain, then fill. A `RecordRef` local inside an `async` method is fine — only one that is still
alive across an `await` is rejected, which is exactly the lifetime rule the buffer needs. The
compiler is enforcing the contract, not merely documenting it.

The same pair is on <xref:DatabentoDotNet.Historical.TimeseriesReader> for historical data, for the
same reason.

## What the compiler cannot enforce

One thing slips through: you can call the decoder again while still holding a `RecordRef` from the
previous call, all within one synchronous method.

```csharp
decoder.TryNextRecord(out var first);
decoder.TryNextRecord(out var second);

// `first` is now stale. It points at buffer bytes that `second` may have overwritten.
// This compiles, runs, and is wrong.
Console.WriteLine(first.Get<TradeMsg>().Price);
```

Nothing in the type system objects, because both refs point into the same live buffer and the
compiler has no idea one of them has been invalidated. **Handle a record before asking for the next
one.** If the natural shape of your code is to hold several records at once, you want the next
section instead.

## When you need a record to outlive the buffer

<xref:DatabentoDotNet.Dbn.OwnedRecord> is the escape hatch: an ordinary class holding its own copy
of the bytes. It has the same reading surface as `RecordRef` — `Get<T>()`, `TryGet<T>()`, `Header`,
`IndexTs` — and none of the lifetime rules, because it does not point at anything shared.

```csharp
// Keep one record beyond the next read.
var kept = OwnedRecord.CopyOf(record);

// Or take the whole stream as an async sequence, one OwnedRecord per record.
await foreach (var owned in reader.ReadRecordsAsync(cancellationToken))
{
    ...
}
```

`ReadRecordsAsync` (and `LiveClient.RecordsAsync`) are the allocating path, and they exist because
allocating is the right answer for a great deal of real code. Buffering a window of records,
handing them to another thread, storing them, or writing straightforward `await foreach` all need
an object that outlives the buffer. **Use them by default** and move to
`FillBufferAsync`/`TryNextRecord` when the allocation shows up in a profile — not before.

An `OwnedRecord` converts back with `AsRef()`, which is what the symbol maps take:

```csharp
if (symbolMap.TryGetSymbol(owned.AsRef(), out var symbol))
{
    ...
}
```

That call is one step that could have been removed with an `OwnedRecord` overload on each map, and
it was deliberately left in place. The distinction between a record that points into the buffer and
a record that owns its bytes is the one thing a caller of this library must keep straight, and
`AsRef()` is where it is visible.

## Record structs are the wire layout

The record types themselves — <xref:DatabentoDotNet.Dbn.TradeMsg>,
<xref:DatabentoDotNet.Dbn.Mbp1Msg>, <xref:DatabentoDotNet.Dbn.InstrumentDefMsg> and the rest — are
`readonly struct`s whose fields are laid out to match the bytes on the wire exactly. A field's type
**is** its wire layout, which has two consequences worth knowing as a consumer:

- **Timestamps are `ulong` nanoseconds and prices are `long` at a fixed 1e-9 scale.** They are not
  `Instant` and not `decimal`, because the wire has eight bytes there and those types are wider.
  See [Time](time.md) for the conversion, and divide prices through `decimal` rather than `double`
  when you need a human-readable figure.
- **Sizes are asserted against `databento-cpp`.** The highest-value test in the repository checks
  `Unsafe.SizeOf<T>()` for every record struct against the `static_assert` values in Databento's
  own C++ client. A layout mistake here would be silent data corruption rather than an exception,
  so it is turned back into a build failure.

## The short version

| You want to | Use |
|---|---|
| Read a record and move on | `TryNextRecord(out var record)`, handle it before the next call |
| Read at maximum throughput, zero allocation | `FillBufferAsync` / `TryNextRecord`, drain-then-fill |
| Keep a record, buffer records, or hand them to another thread | `ReadRecordsAsync` / `RecordsAsync`, or `OwnedRecord.CopyOf` |
| Look a symbol up for an `OwnedRecord` | `map.TryGetSymbol(owned.AsRef(), out var symbol)` |

## See also

- [`samples/DatabentoDotNet.Samples.LiveStream`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.LiveStream) —
  the drain-then-fill loop, running against a real gateway.
- [`samples/DatabentoDotNet.Samples.SymbolResolution`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.SymbolResolution) —
  `OwnedRecord.AsRef()` feeding a `TsSymbolMap`.
- <xref:DatabentoDotNet.Dbn.RecordRef>, <xref:DatabentoDotNet.Dbn.OwnedRecord> in the API reference.
