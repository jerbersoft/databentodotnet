# databentodotnet

A .NET client for [Databento](https://databento.com) market data — real-time streaming and
historical data, with a zero-copy DBN codec at its core.

> **Status: early development.** Milestones 0 (foundation) and 1 (DBN codec) are complete on
> the `m1-dbn-codec` branch — 789 tests, zero warnings. Live streaming (M2) is next. Not yet
> published to NuGet.
>
> - [ROADMAP.md](ROADMAP.md) — milestones, architecture, and design decisions
> - [PORTING.md](PORTING.md) — Rust→.NET mapping guide for the port

## Decoding a DBN stream

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));   // zstd is detected, not declared
Metadata? metadata = decoder.Metadata;

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
        Console.WriteLine($"{trade.Header.TsEvent} {trade.Price} x {trade.Size}");
}
```

Records are reinterpreted **in place** over the read buffer — no allocation per record. That is
why `RecordRef` is a `ref struct` and `TryNextRecord` is synchronous: neither can cross an
`await`, which is the boundary that keeps the zero-copy path sound. A record is valid only until
the next call on the decoder.

Prices are `long` at a fixed 1e-9 scale and timestamps are `ulong` nanoseconds, both deliberately:
`decimal` would cost throughput on the hot path, and a record field's type *is* its wire layout,
so nothing wider than the 8 bytes on the wire can go there.

Above the codec, dates and times are [NodaTime](https://nodatime.org) — `Instant` and
`LocalDate`, never the BCL's `DateTime` family, whose 100 ns tick cannot represent a nanosecond
timestamp at all. `DbnTime` is the single conversion between the two, and it reports DBN's
undefined-timestamp sentinel as absent rather than as a time one nanosecond before the epoch.

## Why this exists

Databento maintains official clients for Python, C++, and Rust — but not .NET. This fills that
gap, with the wire format ported from the normative
[`databento/dbn`](https://github.com/databento/dbn) Rust implementation and struct layouts
pinned against the `static_assert`s in
[`databento-cpp`](https://github.com/databento/databento-cpp).

## Packages

`DatabentoDotNet.*` is used consistently for package IDs, assemblies, and namespaces. This is a
third-party client, so it stays out of `Databento.*` — that is the vendor's namespace, and an
unreserved NuGet prefix they could claim at any time.

| Package / namespace | Contents |
|---|---|
| `DatabentoDotNet.Dbn` | DBN codec: records, metadata, symbol maps |
| `DatabentoDotNet.Live` | Real-time TCP gateway client |
| `DatabentoDotNet.Historical` | Historical HTTPS/REST client |
| `DatabentoDotNet.Reference` | Security master, corporate actions |

```csharp
using DatabentoDotNet.Dbn;
```

## Target frameworks

`net10.0`, with one dependency: `ZstdSharp.Port` for DBN's Zstandard transport compression. It
is pure managed — no P/Invoke, no native asset, no per-RID build — so the package stays trim-
and AOT-friendly.

A `net11.0` target existed briefly, to pick up `System.IO.Compression.ZstandardStream` from the
BCL and ship dependency-free. It was removed in [#16] while .NET 11 is still preview: the
preview SDK is not installed on dev machines, so that code path was compiled nowhere, and CI
inferred the target from the installed SDK — meaning a failed SDK resolution silently dropped it
and the build still passed. An unverifiable branch is worse than one dependency.

Every zstd call routes through a single internal seam, so restoring the target at GA is a
one-file change.

[#16]: https://github.com/jerbersoft/databentodotnet/issues/16

## Building

```sh
dotnet build
dotnet test
```

Requires the .NET 10 SDK or newer. CI builds and tests both target frameworks on Linux, macOS,
and Windows.
