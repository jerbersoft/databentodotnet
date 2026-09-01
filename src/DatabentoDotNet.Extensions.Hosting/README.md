# DatabentoDotNet.Extensions.Hosting

[Databento](https://databento.com) for ASP.NET Core and the .NET generic host:
`IServiceCollection` registration for the historical, reference and live clients,
`IConfiguration` binding, and a hosted live-streaming service with bounded reconnection, an
opt-in health check, and metrics.

> **Ships as `1.1.0`, after `1.0`.** Locking this package's surface to the core four on day one
> would promise it had not built against anything real yet. See `ROADMAP.md` §8 for why, and for
> the `HttpMessageHandler` seam `1.0` shipped on `HistoricalClient` to make this possible.

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting;
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
