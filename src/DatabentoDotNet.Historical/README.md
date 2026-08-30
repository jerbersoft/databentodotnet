# DatabentoDotNet.Historical

The [Databento](https://databento.com) historical HTTPS API for .NET: metadata, timeseries, batch
jobs, and symbology. A port of `databento-rs`'s historical client, all nineteen endpoints.

> **0.9.0 is a beta.** The code is complete and tested; what is not yet settled is whether the
> public surface is the right shape. 1.0.0 undertakes not to break it, so the beta is when
> that undertaking is worth contesting — if something here is awkward to call,
> [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is much cheaper than a
> major version later.

## Price the request before you send it

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;

await using var client = new HistoricalClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};

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

`GetCostAsync` answers, in dollars, what pulling this exact range would cost — before any data moves.
Deliberately not a second parameter set assembled by hand: `GetRangeParams.ToQuery()` renders the
`MetadataQueryParams` for the very request you are about to send, so what was priced and what is sent
cannot drift apart. `SubmitJobParams` carries the same conversion.

The cost comes back as `decimal`, not `double`. The API's own `f64` is a Rust standard-library
limitation rather than a choice, and a per-gigabyte unit price gets multiplied by a record count
before a caller ever sees a figure.

## Dependencies

[NodaTime](https://nodatime.org) for every date range — `DateRange` and `DateTimeRange`, since the
BCL date/time types are banned repo-wide — `Microsoft.Extensions.Logging.Abstractions` for the
optional `LoggerFactory` that surfaces `X-Warning`, plus `DatabentoDotNet.Dbn` and `ZstdSharp.Port`.

## Documentation

The API reference is the XML documentation shipped inside this package. Guides and troubleshooting
are in [the wiki](https://github.com/jerbersoft/databentodotnet/wiki).

Source, issues, and roadmap: <https://github.com/jerbersoft/databentodotnet>.
