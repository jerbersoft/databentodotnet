# DatabentoDotNet.Reference

[Databento](https://databento.com) reference data for .NET: security master, corporate actions, and
adjustment factors. A port of `databento-rs`'s reference client.

> **0.9.1 is a beta.** The code is complete and tested; what is not yet settled is whether the
> public surface is the right shape. 1.0.0 undertakes not to break it, so the beta is when
> that undertaking is worth contesting — if something here is awkward to call,
> [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is much cheaper than a
> major version later.

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Reference;
using NodaTime;

await using var client = new ReferenceClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};

await foreach (var record in client.SecurityMaster.GetRangeAsync(new SecurityMasterGetRangeParams
{
    Symbols = Symbols.From(["AAPL"]),
    StypeIn = SType.RawSymbol,
    DateTimeRange = ReferenceDateTimeRange.Between(
        Instant.FromUtc(2024, 1, 1, 0, 0, 0), Instant.FromUtc(2024, 2, 1, 0, 0, 0)),
}))
{
    Console.WriteLine($"{record.Symbol} {record.SecurityType}");
}
```

## Entitlement

Reference data is a **separate subscription** from live and historical access. A key with full
historical entitlement is still answered `403 license_reference_dataset_no_subscription` on these
endpoints — that is an account matter, not a client error, and the client surfaces it as such.

`AllocateIsins` defaults to `true` on the endpoints that accept it, and on an ISIN-limited plan that
can create new allocations against your quota. Set it deliberately.

## Dependencies

[NodaTime](https://nodatime.org) for `ReferenceDateTimeRange`, whose `Start` and `End` are `Instant`s
because a `DateTimeOffset` cannot round-trip a nanosecond start;
`Microsoft.Extensions.Logging.Abstractions` for the optional `LoggerFactory`; and
`DatabentoDotNet.Historical`, whose transport these endpoints share — which is how upstream builds it
too, the reference API being the historical transport with a different set of slugs.

## Documentation

The API reference is the XML documentation shipped inside this package. Guides and troubleshooting
are at [jerbersoft.github.io/databentodotnet](https://jerbersoft.github.io/databentodotnet/) — start with
[Reference Data](https://jerbersoft.github.io/databentodotnet/guides/reference-data.html).

Source, issues, and roadmap: <https://github.com/jerbersoft/databentodotnet>.
