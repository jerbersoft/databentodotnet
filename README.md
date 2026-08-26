# databentodotnet

A .NET client for [Databento](https://databento.com) market data — real-time streaming and
historical data, with a zero-copy DBN codec at its core.

> **Status: early development.** Milestones 0 (foundation) and 1 (DBN codec) are complete on
> the `m1-dbn-codec` branch — 789 tests, pending a CI run to validate the `net11.0` target,
> which has not yet been compiled anywhere. Live streaming (M2) is next. Not yet published to
> NuGet.
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
`decimal` would cost throughput on the hot path, and `DateTime` ticks are 100 ns and would
silently truncate.

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

Multi-targets `net10.0` and `net11.0`.

.NET 11 adds `System.IO.Compression.ZstandardStream` to the BCL, and DBN uses Zstandard for
transport compression — so on `net11.0` the codec has **no third-party dependencies at all**.
On `net10.0` it falls back to `ZstdSharp.Port`, a pure-managed port with no P/Invoke.

.NET 11 is still preview (GA 2026-11-10), so the `net11.0` target is **enabled automatically
only when an SDK that can build it is installed**. Building with just the .NET 10 SDK works and
produces a `net10.0` build; no configuration needed either way.

## Building

```sh
dotnet build
dotnet test
```

Requires the .NET 10 SDK or newer. CI builds and tests both target frameworks on Linux, macOS,
and Windows.
