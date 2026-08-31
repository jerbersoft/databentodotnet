# Decoding DBN Files

**`new DbnDecoder(stream)` reads `.dbn`, `.dbn.zst`, and `.dbn.frag` from any `Stream`, and
detects Zstandard compression rather than asking you to declare it.** This page covers the
decoder's options, the record downcast, and the version differences that catch people out.

No API key is needed for any of this.

---

## The basic loop

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("trades.dbn.zst"));

Metadata metadata = decoder.Metadata!;
Console.WriteLine($"{metadata.Dataset}, DBN v{metadata.Version}, {metadata.Symbols.Count} symbols");

while (decoder.TryNextRecord(out RecordRef record))
{
    switch (record.Header.RType)
    {
        case RType.Mbp0 when record.TryGet(out TradeMsg trade):
            Handle(trade);
            break;

        case RType.Mbo when record.TryGet(out MboMsg mbo):
            Handle(mbo);
            break;
    }
}
```

`TryNextRecord` returns `false` at end of stream. A stream that ends *between* records is not an
error — files are routinely truncated at a range boundary. A stream that ends part-way *through*
metadata is an error, and the constructor raises `DbnDecodeException` for it.

The `record` is valid only until the next `TryNextRecord`. See [Zero-Copy and
Allocation](zero-copy-and-allocation.md).

## Compression is detected

The constructor peeks four bytes for the Zstandard frame magic and inserts a decompressor if it
finds one. Both branches get the peeked bytes back, so nothing is consumed by the detection.
`decoder.IsCompressed` reports what it decided.

You never pass a `Compression` to the decoder. A file whose name says `.dbn` but whose bytes are
zstd decodes correctly, and so does the reverse.

Compression comes from [`ZstdSharp.Port`](https://www.nuget.org/packages/ZstdSharp.Port) — pure
managed, so trimming and Native AOT stay available.

## Constructor options

```csharp
public DbnDecoder(
    Stream source,
    VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3,
    bool skipMetadata = false,
    byte? inputDbnVersion = null,
    bool tsOut = false,
    bool leaveOpen = false,
    int bufferSize = DbnFsm.DefaultBufferSize)
```

| Parameter | Use |
|---|---|
| `upgradePolicy` | `UpgradeToV3` (default), `UpgradeToV2`, or `AsIs` to see records exactly as written |
| `skipMetadata` | For `.dbn.frag` files, which are records with no metadata block |
| `inputDbnVersion` | The fragment's DBN version. Only read when `skipMetadata` is set |
| `tsOut` | Whether records carry an appended 8-byte `ts_out`. Only read when `skipMetadata` is set |
| `leaveOpen` | Leave `source` open when the decoder is disposed |
| `bufferSize` | The read buffer. The default holds several records of any schema |

### Fragments

A `.dbn.frag` file is a bare record stream with no metadata header — the tail of a larger file, or
a chunk from a batch download. The decoder cannot infer the version or the `ts_out` flag from a
fragment, because the block that states them is exactly what is missing, so you supply both:

```csharp
using var decoder = new DbnDecoder(
    File.OpenRead("chunk.dbn.frag"),
    skipMetadata: true,
    inputDbnVersion: 3,
    tsOut: false);

// decoder.Metadata is null here — there was none to decode.
```

**Getting `tsOut` wrong misreads every record by eight bytes**, and nothing throws. The records
still have plausible-looking headers. If you did not produce the fragment yourself, get the flag
from whatever produced it rather than guessing.

## Getting a typed record out

`RecordRef` is untyped: it is a span over the record's wire bytes plus a `ts_out` flag. Three ways
to narrow it:

```csharp
record.Has<TradeMsg>()                    // would this decode as a TradeMsg?
record.TryGet(out TradeMsg trade)         // copy it out, if so
ref readonly var trade = ref record.Get<TradeMsg>();   // reinterpret in place, throws if not
```

`Get<T>` returns a `ref readonly T` pointing into the buffer and copies nothing. `TryGet` copies
the struct — cheap for a 48-byte `TradeMsg`, less so for a 520-byte `InstrumentDefMsg`, where
`Get` is worth the extra care.

### An rtype alone does not identify a record

Five rtypes decode to a *different struct depending on the record's length*, because those layouts
changed across DBN versions: `InstrumentDef`, `SymbolMapping`, `Error`, `System`, and `Statistics`.

The match rule is `T.HasRType(rtype) && wireLength == T.WireSize`, with **exact** equality. A `>=`
comparison would let a 520-byte v3 `InstrumentDefMsg` match the 360-byte v1 struct and silently
decode as the wrong version. No two versions of the same rtype share a size, so exact equality
disambiguates every family — and `Has`/`TryGet`/`Get` all apply it for you.

This is why `record.TryGet(out InstrumentDefMsg def)` can return `false` on a file that plainly
contains instrument definitions: they are v1 or v2 definitions, and you asked for v3. Either
decode with the default `UpgradeToV3` policy, or ask for `InstrumentDefMsgV1` / `InstrumentDefMsgV2`
explicitly.

### Version upgrade policy

| Policy | Effect |
|---|---|
| `UpgradeToV3` | Default. Older records are widened to v3 layouts as they are decoded |
| `UpgradeToV2` | Widened to v2 |
| `AsIs` | Records arrive in the version they were written in. Ask for the `V1`/`V2` structs |

`UpgradeToV3` is the right default and matches upstream. Use `AsIs` when you are inspecting a file
rather than consuming it — a conformance test, a format investigation, a bug report.

## Metadata

`decoder.Metadata` is populated by the constructor and is `null` only for a fragment.

```csharp
var m = decoder.Metadata!;

m.Version            // 1, 2, or 3
m.Dataset            // "GLBX.MDP3"
m.Schema             // Schema? — null for a mixed-schema file
m.Start, m.End       // ulong nanoseconds; End is null when open-ended
m.Limit              // ulong? record cap, if the query had one
m.StypeIn, m.StypeOut
m.TsOut              // whether every record carries an appended ts_out
m.Symbols            // the symbols requested
m.Partial            // requested, resolved for part of the range only
m.NotFound           // requested, never resolved
m.Mappings           // instrument_id ↔ symbol, with date intervals
```

**`Schema` is nullable and that is not defensive.** A file assembled from several queries carries
no single schema, and the field is genuinely absent. Branch on `record.Header.RType` rather than
on the metadata's schema when you need to know what a record is.

`Partial` and `NotFound` are worth checking before you conclude a symbol has no data. A symbol in
`NotFound` was never resolved at all — usually a typo, or an `stype_in` mismatch.

## Records you will meet

Twenty-one record structs, all `readonly struct`, all reinterpreted in place. The common ones:

| Struct | Schema | Notes |
|---|---|---|
| `TradeMsg` | `trades` | Indexes on `TsRecv` |
| `MboMsg` | `mbo` | Every order-book event. The densest schema DBN defines |
| `Mbp1Msg`, `Mbp10Msg` | `mbp-1`, `mbp-10` | Book snapshots with 1 or 10 levels |
| `BboMsg`, `CbboMsg`, `Cmbp1Msg` | `bbo-1s`, `cbbo-1s`, `cmbp-1` | Best bid/offer, per-venue and consolidated |
| `OhlcvMsg` | `ohlcv-*` | One struct for every bar interval |
| `InstrumentDefMsg` | `definition` | Plus `V1` and `V2`. 520 bytes in v3 |
| `StatusMsg` | `status` | Trading-session state changes |
| `ImbalanceMsg` | `imbalance` | Auction imbalance |
| `StatMsg` | `statistics` | Plus `V1` |
| `SymbolMappingMsg` | — | Sent in-band. Feed to a `PitSymbolMap` |
| `SystemMsg` | — | Heartbeats and gateway notices. Check `.Code` |
| `ErrorMsg` | — | Gateway errors |

Every one of them has a `WireSize` asserted against the `static_assert` values in `databento-cpp`
by a test that runs on every build. Records are reinterpreted over the read buffer, so a layout
mistake is silent data corruption rather than an exception — those assertions turn it back into a
build failure.

## Text fields

Symbols and other text arrive as fixed-width NUL-padded C-string fields — `CStr71` for a v2+
symbol, `CStr22` for a v1 one. They live inside the record's own bytes and are **not** decoded to
a `string` as part of decoding:

```csharp
ReadOnlySpan<byte> bytes = def.RawSymbol.AsTextSpan();   // allocation-free, NUL padding stripped
string symbol = def.RawSymbol.ToString();                // allocates — only when you ask
```

Compare against `AsTextSpan()` in a hot loop and call `ToString()` only when you are about to
display or store the value. A decoder that materialised a `string` per record would allocate per
record, which is the whole thing this library is built to avoid.

A field whose text fills all N bytes has no room for a terminator. This library returns the full
field in that case, matching `databento-cpp`; upstream Rust rejects it. The divergence only ever
adds characters that are genuinely on the wire.

## See also

- [Timestamps and Prices](timestamps-and-prices.md) — `IndexTs`, the 1e-9 price scale, and the sentinels
- [Symbol Resolution](symbol-resolution.md) — using `Metadata.Mappings` and `SymbolMappingMsg`
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — why `RecordRef` cannot be stored
