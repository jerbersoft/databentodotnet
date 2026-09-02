# DatabentoDotNet.Extensions.Hosting

[Databento](https://databento.com) for ASP.NET Core and the .NET generic host:
`IServiceCollection` registration for the historical, reference and live clients,
`IConfiguration` binding, and a hosted live-streaming service with bounded reconnection, an
opt-in health check, and metrics.

> **`0.10.0` is this package's first release, and it carries no promise.** The four packages
> beside it spent `0.9.0` and `0.9.1` on nuget.org, installable and explicitly unpromised, and that
> is what earned them a `1.0`. This one has had no such window yet, so it gets the same one rather
> than a SemVer guarantee on the day it appears. Pin the exact version, and if something here is
> awkward to call, [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is far
> cheaper than a major version later — that is what this release is for.

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDatabento();
builder.Services.AddDatabentoLive().AddRecordHandler<TradePrinter>();

using var host = builder.Build();
await host.RunAsync();

internal sealed class TradePrinter : ILiveRecordHandler
{
    public void OnRecord(scoped RecordRef record)
    {
        if (record.TryGet(out TradeMsg trade))
            Console.WriteLine($"{record.Header.InstrumentId} {trade.Price} x {trade.Size}");
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

```json
{
  "Databento": {
    "Live": {
      "Default": {
        "Dataset": "EQUS.MINI",
        "Subscriptions": [{ "Schema": "trades", "Symbols": ["AAPL", "MSFT"] }]
      }
    }
  }
}
```

No API key in that file — it comes from `Databento:ApiKey`, or from the `DATABENTO_API_KEY`
environment variable if that key is left unset too.

## Dependencies

The four core packages, all four reached transitively so that registering `HistoricalClient` alone
still works with one package reference. Plus `Microsoft.Extensions.Hosting.Abstractions`,
`Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Extensions.Http` (the seam
behind a pooled `HistoricalClient`), and `Microsoft.Extensions.Diagnostics.HealthChecks` (only
reached by code if you call `AddHealthCheck`).

## Documentation

The API reference is the XML documentation shipped inside this package. The full configuration
shape, writing a handler, running more than one session, and reconnection are at
[jerbersoft.github.io/databentodotnet](https://jerbersoft.github.io/databentodotnet/) — start with
[Hosting and Dependency Injection](https://jerbersoft.github.io/databentodotnet/guides/hosting-and-dependency-injection.html).

Source, issues, and roadmap: <https://github.com/jerbersoft/databentodotnet>.
