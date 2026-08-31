# Symbol Resolution

**Records carry a numeric `instrument_id`, not a ticker.** Turning one back into `AAPL` or
`ESZ5` means consulting a symbol map — and which map you want depends on whether your data spans
one day or many.

Two implementations, one interface. Both are `Try*` throughout, because a miss is routine rather
than exceptional.

---

## Which map

| | `PitSymbolMap` | `TsSymbolMap` |
|---|---|---|
| Keyed on | `instrument_id` alone | `(date, instrument_id)` |
| Built from | Metadata for one date, **or** grown record by record | Metadata for the whole range |
| Use for | **Live sessions**, and single-day historical requests | Historical requests spanning several days |
| Cost | One entry per instrument | One entry per instrument **per day** |

The deciding question is whether the same `instrument_id` can mean different things across your
data. It can: a continuous contract rolling to a new front month keeps its ID and changes its
symbol. Over one day it cannot, which is what makes the cheaper point-in-time map correct there.

**`PitSymbolMap` ignores the record's timestamp entirely, and that is the design.** You committed
to a date when you built it. A point-in-time map that started consulting the record's date would
not be point-in-time any more.

## Live: a map that grows with the stream

A live gateway sends `SymbolMappingMsg` records in-band, as assignments happen. Feed each record
to the map and resolve against it:

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;

var symbols = new PitSymbolMap();

var metadata = await client.StartAsync(ct);

while (true)
{
    while (client.TryNextRecord(out RecordRef record))
    {
        symbols.OnRecord(record);        // no-op unless it is a mapping or definition record

        if (record.TryGet(out TradeMsg trade))
        {
            symbols.TryGetSymbol(trade.Header.InstrumentId, out var symbol);
            Console.WriteLine($"{symbol ?? "?"}  {trade.Price / 1e9:F4} x {trade.Size}");
        }
    }

    if (await client.FillBufferAsync(ct) == 0) { break; }
}
```

`OnRecord` inspects the record type and updates the map from `SymbolMappingMsg` or
`InstrumentDefMsg`; anything else is ignored. It is called once per record and stays
allocation-light — it allocates exactly the one `string` a genuinely new mapping requires, and
nothing else.

**Early records will not resolve.** The map holds nothing for an instrument until its mapping
record arrives, and mapping records are not guaranteed to come first. `TryGetSymbol` returning
`false` at the start of a session is normal; handle it rather than treating it as an error.

If you subscribed with `StypeIn = SType.InstrumentId` you already know the IDs and may not need a
map at all.

## Historical: a map built from metadata

```csharp
using var decoder = new DbnDecoder(File.OpenRead("range.dbn.zst"));

var symbols = TsSymbolMap.FromMetadata(decoder.Metadata!);

while (decoder.TryNextRecord(out RecordRef record))
{
    if (symbols.TryGetSymbol(record, out var symbol))
    {
        Handle(symbol, record);
    }
}
```

The `TryGetSymbol(RecordRef, …)` overload takes the record's **index date** — derived from
`IndexTs`, not from `Header.TsEvent` — so you do not have to know which timestamp each record type
indexes on. That matters: `ts_event` and `ts_recv` can fall on opposite sides of UTC midnight, and
keying on the wrong one silently returns the previous day's symbol with nothing looking broken.
See [Timestamps and Prices](timestamps-and-prices.md).

For a single-day file, `PitSymbolMap.FromMetadata(metadata, date)` is smaller and faster. Unlike
`TsSymbolMap.FromMetadata`, it validates that the date falls inside the metadata's own query
range.

### Storage cost

`TsSymbolMap` expands each `[startDate, endDate)` interval into **one dictionary entry per
calendar day**, trading memory for an O(1) exact-date lookup with no range search. Upstream makes
the same trade.

A query covering many instruments over a wide range therefore builds a large map. That is
expected, not a leak. If it becomes a problem, either narrow the range or use a `PitSymbolMap`
per day.

The symbol `string` itself is shared across every day of an interval rather than copied, so the
per-day cost is a dictionary entry and a reference, not a string.

## Writing code that takes either

Both implement `ISymbolIndex`:

```csharp
void Consume(ISymbolIndex symbols, RecordRef record)
{
    if (symbols.TryGetSymbol(record, out var symbol))
    {
        // …
    }
}
```

The two answer differently — one keys on the record's date, one ignores it — and that difference
is deliberate. Write against the interface when the caller decides which map to hand you; reach
for the concrete type when you specifically need `OnRecord` or a date-explicit lookup.

**There is no indexer on either type.** Upstream pairs its lookups with `Index` impls that panic
on a miss, and a C# indexer carries the same expectation, since that is what `Dictionary` does.
But a miss here is ordinary — a live stream resolves nothing for an instrument until its mapping
arrives, and a timeseries map holds nothing for a date outside the query range. An indexer would
make the common case throw, so the whole surface is `Try*`.

## Going the other way

To subscribe or query by symbol rather than by ID, you do not need a map — you need the right
`stype_in`:

```csharp
Symbols.From(["AAPL", "MSFT"])          // StypeIn = SType.RawSymbol   (the default)
Symbols.From(["ES.FUT"])                // StypeIn = SType.Parent      — every ES contract
Symbols.From(["ES.c.0"])                // StypeIn = SType.Continuous  — the front month
Symbols.FromIds([12345u])               // StypeIn = SType.InstrumentId
Symbols.All                             // everything the dataset carries
```

`Metadata.Mappings`, `Metadata.Partial`, and `Metadata.NotFound` report what the gateway made of
what you asked for. A symbol in `NotFound` was never resolved at all — usually a typo, or an
`stype_in` that does not match the symbols you supplied.

## See also

- [Timestamps and Prices](timestamps-and-prices.md) — `IndexTs`, and the UTC-midnight failure this page keeps referring to
- [Live Streaming](live-streaming.md) — subscriptions and `stype_in`
- [Decoding DBN Files](decoding-dbn-files.md) — `Metadata.Mappings` and the rest of the header
