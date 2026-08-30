# DatabentoDotNet.Dbn

A zero-copy codec for **DBN** (Databento Binary Encoding), the wire and file format
[Databento](https://databento.com) uses for market data. Records, metadata, symbol maps, and a
decoder that reads `.dbn`, `.dbn.zst`, and `.dbn.frag`.

This is a third-party client. Databento ships official clients for Python, C++, and Rust — but not
.NET, so this fills that gap, ported from the normative
[`databento/dbn`](https://github.com/databento/dbn) Rust implementation with struct layouts pinned
against the `static_assert`s in [`databento-cpp`](https://github.com/databento/databento-cpp).

> **0.9.0 is a beta.** The code is complete and tested; what is not yet settled is whether the
> public surface is the right shape. 1.0.0 undertakes not to break it, so the beta is when
> that undertaking is worth contesting — if something here is awkward to call,
> [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is much cheaper than a
> major version later.

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));   // zstd is detected, not declared
Metadata? metadata = decoder.Metadata;

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
}
```

`IndexTs`, not `Header.TsEvent`. Most schemas — trades included — index on `ts_recv`, and the two
can fall on opposite sides of UTC midnight, so keying a symbol lookup on `ts_event` silently returns
the previous day's symbol with nothing looking broken.

## What "zero-copy" means here, and what it costs you

Records are reinterpreted **in place** over the read buffer — nothing is allocated per record, which
is asserted by a test rather than measured by a benchmark. That is why `RecordRef` is a `ref struct`
and why `TryNextRecord` is synchronous: neither can cross an `await`, which is the boundary that
keeps the reinterpretation sound. **A record is valid only until the next call on the decoder.**

Prices are `long` at a fixed 1e-9 scale and timestamps are `ulong` nanoseconds, both deliberately: a
record field's type *is* its wire layout, so nothing wider than the eight bytes on the wire can go
there.

Above the codec, dates and times are [NodaTime](https://nodatime.org) — `Instant` and `LocalDate`,
never the BCL's `DateTime` family, whose 100 ns tick cannot represent a nanosecond timestamp at all.
`DbnTime` is the single conversion between the two, and it reports DBN's undefined-timestamp
sentinel as absent rather than as a time one nanosecond before the epoch.

## Dependencies

[NodaTime](https://nodatime.org) and `ZstdSharp.Port`. The latter is pure managed — no P/Invoke, no
native asset, no per-RID build — so the package is trim- and Native-AOT-friendly, verified by
publishing and *running* an AOT binary rather than by analyzers alone.

## Documentation

The API reference is the XML documentation shipped inside this package, so it reaches IntelliSense at
the call site. Guides and explanations are in
[the wiki](https://github.com/jerbersoft/databentodotnet/wiki) — start with
[Zero-Copy and Allocation](https://github.com/jerbersoft/databentodotnet/wiki/Zero-Copy-and-Allocation)
and [Timestamps and Prices](https://github.com/jerbersoft/databentodotnet/wiki/Timestamps-and-Prices).

Source, issues, and roadmap: <https://github.com/jerbersoft/databentodotnet>.
