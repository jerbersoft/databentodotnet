# Getting started: `DatabentoDotNet.Dbn`

The DBN codec — record structs, metadata, the decoder, and symbol maps. Every other package in this
library depends on it, and it depends on none of them: if you have `.dbn` or `.dbn.zst` files
already and no need to call an API, this is the only package you need.

```sh
dotnet add package DatabentoDotNet.Dbn
```

Dependencies: [NodaTime](https://nodatime.org) and `ZstdSharp.Port`. Both are pure managed, so the
package is trim- and Native-AOT-friendly with no native asset and no per-RID build.

## Decode a file

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
    {
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)}  {trade.Price} x {trade.Size}");
    }
}
```

**Compression is detected, not declared.** The constructor sniffs the stream, so the same code
opens `.dbn` and `.dbn.zst`; `DbnDecoder.IsCompressed` reports what it found. (The one place this
does *not* hold is <xref:DatabentoDotNet.Historical.TimeseriesClient.OpenFileAsync*>, which
decompresses unconditionally because it exists to reopen what `GetRangeToFileAsync` wrote. Hand it
a plain `.dbn` and zstd reports "unknown frame descriptor".)

> [!IMPORTANT]
> `record` is valid only until the next call to `TryNextRecord`. It is a `ref struct` pointing into
> the decoder's buffer, not a copy. See [the zero-copy contract](zero-copy.md) — it is the one page
> to read before writing anything real against this package.

## Getting at the fields

<xref:DatabentoDotNet.Dbn.RecordRef> is schema-agnostic; the typed structs are where the fields
are. Three ways in, depending on what you know:

```csharp
// You expect this type, and a different one is a bug.
TradeMsg trade = record.Get<TradeMsg>();

// The stream carries several types and you are picking them off.
if (record.TryGet<TradeMsg>(out var trade)) { ... }

// Just asking.
bool isTrade = record.Has<TradeMsg>();
```

The type parameter is checked against the record's `rtype` on the wire, so `TryGet` is a comparison
and a reinterpret — no copy, no allocation.

One wire detail worth knowing early: **`RType` is not the schema's name for the record.** A trade
arrives as `RType.Mbp0` — market-by-price carrying zero book levels — which is correct and looks
wrong the first time you print it.

## Timestamps and prices

```csharp
// Nanoseconds since the Unix epoch, as a ulong, because that is what the wire carries.
ulong raw = record.IndexTs;

// NodaTime above the wire. TryToInstant reports DBN's "undefined" sentinel as absent
// rather than as a time one nanosecond before the epoch.
if (DbnTime.TryToInstant(raw, out var when)) { ... }

// Prices are long at a fixed 1e-9 scale, with their own sentinel.
if (trade.Price != DbnConstants.UndefPrice)
{
    decimal price = (decimal)trade.Price / DbnConstants.FixedPriceScale;
}
```

Divide through `decimal`, not `double`. And prefer `IndexTs` over `Header.TsEvent` for anything
date-keyed — most schemas index on `ts_recv`, and the two can land on opposite sides of UTC
midnight. [Time](time.md) has the full argument, the sentinel hazard, and the conversion table.

## Metadata

Every DBN stream opens with a metadata header, decoded before the first record and available from
`decoder.Metadata`:

```csharp
Metadata? metadata = decoder.Metadata;

Console.WriteLine(metadata!.Version);      // 1, 2 or 3 — the DBN version on the wire
Console.WriteLine(metadata.Dataset);       // "GLBX.MDP3"
Console.WriteLine(metadata.Schema);        // Schema.Trades, or null for a mixed stream
Console.WriteLine(metadata.StypeOut);      // SType.InstrumentId
Console.WriteLine(metadata.Mappings.Count);
```

`Metadata` is nullable on the decoder because a `.dbn.frag` fragment has no header. It is also
where `NotFound` and `Partial` live — the symbols the server could not resolve at all, and the ones
it resolved for only part of the requested range. **Both are easy to skip and worth checking**: a
misspelled symbol produces an empty result rather than an error.

By default the decoder upgrades older streams to the current version, so a DBN v1 file decodes into
v3 structs and your code does not branch on `Metadata.Version`. Pass a different
`VersionUpgradePolicy` to the constructor if you need the original layout.

## Symbols

Records carry a numeric `instrument_id`, not a ticker. Two maps turn one into the other, and which
you want depends on how much history is in front of you:

```csharp
// Time-series: the whole requested range, built from the metadata's mappings.
// An instrument id means different things on different days, so lookups are date-keyed.
var tsMap = TsSymbolMap.FromMetadata(metadata);
if (tsMap.TryGetSymbol(record, out var symbol)) { ... }

// Point-in-time: one day's mappings, kept current by feeding it records as they arrive.
// This is the live-stream shape — mappings show up interleaved with the data.
var pitMap = PitSymbolMap.FromMetadata(metadata, date);
pitMap.OnRecord(record);
```

Both `TryGetSymbol` overloads that take a record use `IndexTs` internally, which is the reason the
previous section cares about `IndexTs` versus `TsEvent`.

## Where to go next

- [The zero-copy contract](zero-copy.md) — required reading before anything performance-shaped.
- [Time](time.md) — the NodaTime boundary, the sentinel, and `IndexTs`.
- [`samples/DatabentoDotNet.Samples.SymbolResolution`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.SymbolResolution) —
  a runnable program doing the symbol lookup above against real data.
- <xref:DatabentoDotNet.Dbn.DbnDecoder>, <xref:DatabentoDotNet.Dbn.Metadata>,
  <xref:DatabentoDotNet.Dbn.TsSymbolMap> in the API reference.
