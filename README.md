# databentodotnet

A .NET client for [Databento](https://databento.com) market data — real-time streaming and
historical data, with a zero-copy DBN codec at its core.

> **Status: early development.** Milestones 0 (foundation) and 1 (DBN codec) are complete and
> merged to `master` — 832 tests, zero warnings. Live streaming (M2) is in progress. Not yet
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

// The same MetadataQueryParams value a timeseries.get_range call will take, once #38 ships —
// get_cost prices the exact request you're about to send, not an approximation of it.
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
moves. `MetadataQueryParams` is deliberately the one type both `get_cost` and (from #38)
`timeseries.get_range` take: price a request, then send the very same value, rather than a
second one assembled by hand that might not match what was priced. The cost comes back as
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
