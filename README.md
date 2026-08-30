# databentodotnet

[![CI](https://github.com/jerbersoft/databentodotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/jerbersoft/databentodotnet/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/jerbersoft/databentodotnet?color=0066cc)](https://github.com/jerbersoft/databentodotnet/releases)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-blueviolet)](https://dotnet.microsoft.com/download/dotnet)

A .NET client for [Databento](https://databento.com) market data — real-time streaming, historical
data, and reference data, with a zero-copy DBN codec at its core.

> **Status: code complete, in beta at 0.9.0.** The DBN codec, live streaming, the historical client
> and reference data are all merged to `master` and **published to NuGet at `0.9.0`** — 1,868 tests,
> zero warnings, a public API locked by an analyzer, and full method-level parity with
> `databento-rs`. It is `0.9.0` rather than 1.0.0 deliberately: 1.0.0 undertakes not to break a
> 3,801-member public surface, and that undertaking is cheap to make and expensive to withdraw.
> **The beta is where that surface gets contested** — if something in it is awkward to use,
> [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is far cheaper than a major
> version later. Tracked as [#74], with 1.0.0 as [#68].
>
> The NuGet badges below read the live feed, so they are the authority on what is actually published.
>
> - [Documentation](#documentation) — the wiki: guides, explanations, and troubleshooting
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
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
}
```

`IndexTs`, not `Header.TsEvent`. Most schemas — trades included — index on `ts_recv`, and the
two can fall on opposite sides of UTC midnight, so keying a symbol lookup on `ts_event` silently
returns the previous day's symbol with nothing looking broken. `RecordRef.IndexTs` picks the
right field per record type.

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

## Pricing a request before you send it

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;

await using var client = new HistoricalClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};

// get_cost prices the exact request you're about to send, not an approximation of it. A
// timeseries.get_range call renders this same value with GetRangeParams.ToQuery().
var request = new MetadataQueryParams
{
    Dataset = "XNAS.ITCH",
    Symbols = Symbols.From(["AAPL", "MSFT"]),
    Schema = Schema.Trades,
    DateTimeRange = DateTimeRange.Between(
        Instant.FromUtc(2023, 7, 1, 0, 0, 0), Instant.FromUtc(2023, 8, 1, 0, 0, 0)),
};

decimal cost = await client.Metadata.GetCostAsync(request);
if (cost > 5.00m)
{
    Console.WriteLine($"${cost} for that range — narrowing it before pulling any data.");
    return;
}
```

`get_cost` answers, in dollars, what pulling this exact range would cost — before any data
moves. That is deliberately not a second parameter set assembled by hand: `GetRangeParams.ToQuery()`
renders the `MetadataQueryParams` for the very request you are about to send, so what was priced and
what is sent cannot drift apart. `SubmitJobParams` carries the same conversion. The cost comes back as
`decimal`, not `double` — the API's own `f64` is a Rust standard-library limitation rather than
a choice, and a per-gigabyte unit price gets multiplied by a record count before a caller ever
sees a figure.

## Samples

Four runnable console programs live under [`samples/`](samples) — a live stream, a historical range,
a batch download, and symbol resolution applied to decoded records. Each takes its key from
`DATABENTO_API_KEY`, each runs with no arguments, and each says what it costs before it spends
anything.

```sh
export DATABENTO_API_KEY=db-...
dotnet run --project samples/DatabentoDotNet.Samples.HistoricalRange
```

See [samples/README.md](samples/README.md) for what each one shows and what it costs to run.

## Documentation

**[The wiki](https://github.com/jerbersoft/databentodotnet/wiki) is the documentation** — guides,
explanations, troubleshooting, and the release narrative. Start at
[Getting Started](https://github.com/jerbersoft/databentodotnet/wiki/Getting-Started); the
[style guide](https://github.com/jerbersoft/databentodotnet/wiki/Wiki-Style-Guide) explains what
lives there versus here, and why one canonical copy of each fact is the rule.

The two pages worth reading before writing anything real:
[Zero-Copy and Allocation](https://github.com/jerbersoft/databentodotnet/wiki/Zero-Copy-and-Allocation)
(a `RecordRef` is valid until the next decoder call, and breaking that reads stale bytes rather than
throwing) and
[Timestamps and Prices](https://github.com/jerbersoft/databentodotnet/wiki/Timestamps-and-Prices)
(nanoseconds, NodaTime, and the three sentinels).

**The API reference is the XML documentation comments**, which `dotnet pack` ships inside the
package — so every member's docs reach IntelliSense in your editor, at the call site, with nothing
to publish and nothing that can drift. There is deliberately no separate documentation site; see
[#70](https://github.com/jerbersoft/databentodotnet/issues/70).

For contributing: [CLAUDE.md](CLAUDE.md) · [PORTING.md](PORTING.md) · [ROADMAP.md](ROADMAP.md)

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

| Package / namespace | NuGet | Contents |
|---|---|---|
| `DatabentoDotNet.Dbn` | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Dbn.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Dbn) | DBN codec: records, metadata, symbol maps |
| `DatabentoDotNet.Live` | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Live.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Live) | Real-time TCP gateway client |
| `DatabentoDotNet.Historical` | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Historical.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Historical) | Historical HTTPS/REST client |
| `DatabentoDotNet.Reference` | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Reference.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Reference) | Security master, corporate actions |

### Install

All packages are published to [nuget.org](https://www.nuget.org):

```bash
dotnet add package DatabentoDotNet.Dbn
dotnet add package DatabentoDotNet.Live
dotnet add package DatabentoDotNet.Historical
dotnet add package DatabentoDotNet.Reference
```

```csharp
using DatabentoDotNet.Dbn;
```

## Status

| | Status |
|---|---|
| **CI** | [![CI](https://github.com/jerbersoft/databentodotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/jerbersoft/databentodotnet/actions/workflows/ci.yml) |
| **Native AOT** | [![Native AOT](https://github.com/jerbersoft/databentodotnet/actions/workflows/aot.yml/badge.svg)](https://github.com/jerbersoft/databentodotnet/actions/workflows/aot.yml) |
| **Publish** | [![Publish NuGet](https://github.com/jerbersoft/databentodotnet/actions/workflows/publish.yml/badge.svg)](https://github.com/jerbersoft/databentodotnet/actions/workflows/publish.yml) |
| **License** | [![Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE) |
| **Latest** | [![GitHub Release](https://img.shields.io/github/v/release/jerbersoft/databentodotnet?sort=semver)](https://github.com/jerbersoft/databentodotnet/releases) |

## Target frameworks

`net10.0`, with three public dependencies across the four packages, each a deliberate cost rather
than an accident: [NodaTime](https://nodatime.org) for all date and time handling,
`ZstdSharp.Port` for DBN's Zstandard transport compression, and
`Microsoft.Extensions.Logging.Abstractions` for the two HTTP clients' optional `LoggerFactory`.
`ZstdSharp.Port` is pure managed — no P/Invoke, no native asset, no per-RID build — so the
packages stay trim- and AOT-friendly, which is verified by publishing and *running* a Native AOT
binary rather than by the analyzers alone.

A `net11.0` target existed briefly, to pick up `System.IO.Compression.ZstandardStream` from the
BCL and ship dependency-free. It was removed in [#16] while .NET 11 is still preview: the
preview SDK is not installed on dev machines, so that code path was compiled nowhere, and CI
inferred the target from the installed SDK — meaning a failed SDK resolution silently dropped it
and the build still passed. An unverifiable branch is worse than one dependency.

Every zstd call routes through a single internal seam, so restoring the target at GA is a
one-file change.

[#16]: https://github.com/jerbersoft/databentodotnet/issues/16
[#65]: https://github.com/jerbersoft/databentodotnet/issues/65
[#68]: https://github.com/jerbersoft/databentodotnet/issues/68
[#74]: https://github.com/jerbersoft/databentodotnet/issues/74

## Building

```sh
dotnet build
dotnet test
```

Requires the .NET 10 SDK or newer. CI builds and tests on Linux, macOS, and Windows, and a
separate workflow publishes and runs a Native AOT binary on every push.
