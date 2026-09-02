---
_layout: landing
---

<div class="text-center my-5">
  <h1 class="display-4 fw-bold">DatabentoDotNet</h1>
  <p class="lead">
    A .NET client for <a href="https://databento.com">Databento</a> market data — real-time
    streaming, historical queries, and a zero-copy DBN codec.
  </p>
  <p>
    <a class="btn btn-primary btn-lg" href="guides/getting-started.md">Get started</a>
    <a class="btn btn-outline-secondary btn-lg" href="api/index.md">API reference</a>
    <a class="btn btn-outline-secondary btn-lg" href="https://github.com/jerbersoft/databentodotnet">GitHub</a>
  </p>
</div>

> [!NOTE]
> This is a third-party client. It is not published or endorsed by Databento, who ship official
> clients for Python, C++ and Rust. **`0.10.0` is a beta**: the code is complete and tested, and
> what is not yet settled is whether the public surface is the right shape. The hosting extensions
> are newest — `0.10.0` is their first release.

## Install

Five packages, and you take only the ones you use. `DatabentoDotNet.Dbn` is the only one with no
sibling dependency; every other one brings it in.

| Package | Latest | Contents |
|---|---|---|
| [`DatabentoDotNet.Dbn`](https://www.nuget.org/packages/DatabentoDotNet.Dbn) | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Dbn.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Dbn) | The DBN codec — record structs, metadata, decoder, symbol maps |
| [`DatabentoDotNet.Live`](https://www.nuget.org/packages/DatabentoDotNet.Live) | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Live.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Live) | Real-time and intraday-replay streaming over the raw TCP gateway |
| [`DatabentoDotNet.Historical`](https://www.nuget.org/packages/DatabentoDotNet.Historical) | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Historical.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Historical) | Historical HTTPS API — timeseries, batch, symbology, metadata |
| [`DatabentoDotNet.Reference`](https://www.nuget.org/packages/DatabentoDotNet.Reference) | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Reference.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Reference) | Security master, corporate actions, adjustment factors |
| [`DatabentoDotNet.Extensions.Hosting`](https://www.nuget.org/packages/DatabentoDotNet.Extensions.Hosting) | [![NuGet](https://img.shields.io/nuget/v/DatabentoDotNet.Extensions.Hosting.svg?color=004880)](https://www.nuget.org/packages/DatabentoDotNet.Extensions.Hosting) | ASP.NET Core and generic-host registration, and a hosted live session |

The badges read the live feed, so they are the authority on what is actually published — not this
page, and not the version named in the note above.

```sh
dotnet add package DatabentoDotNet.Dbn          # the DBN codec
dotnet add package DatabentoDotNet.Live         # real-time streaming
dotnet add package DatabentoDotNet.Historical   # historical HTTPS API
dotnet add package DatabentoDotNet.Reference    # security master, corporate actions
dotnet add package DatabentoDotNet.Extensions.Hosting   # ASP.NET Core / generic host
```

Requires .NET 10 or newer. Every date and time on the public surface is
[NodaTime](https://nodatime.org) — an `Instant`, never a `DateTime`, because a `DateTime` tick is
100 nanoseconds and a DBN timestamp is one.

## Stream live trades

Four calls, because the protocol has four steps — and `StartAsync` is where billing begins.
Nothing before it moves market data.

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;

await using var client = new LiveClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
    Dataset = "EQUS.MINI",
};

await client.ConnectAsync();
await client.AuthenticateAsync();
await client.SubscribeAsync(new Subscription
{
    Schema = Schema.Trades,
    Symbols = Symbols.From(["AAPL", "MSFT"]),
});

await client.StartAsync();

while (true)
{
    // Drain what the last read produced before asking the socket for more: a refill may move the
    // buffer, and that is what ends a RecordRef's life.
    while (client.TryNextRecord(out RecordRef record))
    {
        if (record.TryGet(out TradeMsg trade))
        {
            Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
        }
    }

    if (await client.FillBufferAsync() == 0)
    {
        break;   // the gateway closed the stream
    }
}

await client.CloseAsync();
```

## Run that session inside a host

The loop above, lifted into the .NET generic host. The four protocol calls become one registration,
the session's shape moves to `appsettings.json`, and a dropped connection is retried with bounded
backoff instead of ending the program.

```csharp
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDatabento();
builder.Services.AddDatabentoLive("equities")
    .AddRecordHandler<TradePrinter>()
    .AddHealthCheck();       // opt-in — nothing registers one for you

using var host = builder.Build();
await host.RunAsync();       // connecting through starting happens in here, and billing with it

internal sealed class TradePrinter : ILiveRecordHandler
{
    // Not async, and it cannot be: a ref struct crosses neither an await nor a yield return. The
    // record points into the session's read buffer and is valid for this call only, so copy out
    // what you need and let OnFlushAsync do anything that has to await.
    public void OnRecord(scoped RecordRef record)
    {
        if (record.TryGet(out TradeMsg trade))
        {
            Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

```json
{
  "Databento": {
    "Live": {
      "equities": {
        "Dataset": "EQUS.MINI",
        "Subscriptions": [
          { "Schema": "trades", "StypeIn": "raw_symbol", "Symbols": ["AAPL", "MSFT"] }
        ],
        "Reconnect": { "Enabled": true, "InitialDelay": "PT1S", "MaxDelay": "PT30S", "MaxAttempts": 10 }
      }
    }
  }
}
```

`DatabentoDotNet.Extensions.Hosting` references only the *abstractions* half of
`Microsoft.Extensions.Hosting`, so a plain console app needs that package too; the Worker and Web
SDKs already carry it.

The key is not in that file and should not be: the chain is the session's own `ApiKey`, then
`Databento:ApiKey`, then the `DATABENTO_API_KEY` environment variable. Durations are ISO-8601
strings because the BCL's `TimeSpan` is banned repo-wide, and an options DTO therefore cannot carry
one. A session that names a schema that is not a schema fails at startup rather than on first read.

[Hosting and Dependency Injection](guides/hosting-and-dependency-injection.md) is the full guide.

## Download a historical range

Ask the price before taking the data. `metadata.get_cost` is free, and `ToQuery()` renders it for
the very request you are about to send — so what was priced and what is sent cannot drift apart.

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;

await using var client = new HistoricalClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};

var request = new GetRangeParams
{
    Dataset = "GLBX.MDP3",
    Symbols = Symbols.From("ESH4"),
    Schema = Schema.Trades,
    DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
    Limit = 10,
};

decimal cost = await client.Metadata.GetCostAsync(request.ToQuery());
if (cost > 0.01m)
{
    Console.WriteLine($"${cost} is more than this program will spend.");
    return;
}

await using var reader = await client.Timeseries.GetRangeAsync(request);
await foreach (OwnedRecord record in reader.ReadRecordsAsync())
{
    if (record.TryGet(out TradeMsg trade))
    {
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
    }
}
```

## Decode a local file

No key, no network. `.dbn`, `.dbn.zst` and `.dbn.frag`, with Zstandard framing detected rather than
declared.

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));

Metadata metadata = decoder.Metadata!;
Console.WriteLine($"DBN v{metadata.Version} {metadata.Dataset}");

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
    {
        // Prices are fixed-point integers at a 1e-9 scale. Dividing through decimal keeps the
        // exact value; dividing through double would not.
        decimal price = (decimal)trade.Price / DbnConstants.FixedPriceScale;
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {price} x {trade.Size}");
    }
}
```

`IndexTs`, not `Header.TsEvent`. Most schemas — trades included — index on `ts_recv`, and the two
can fall on opposite sides of UTC midnight, so keying a symbol lookup on `ts_event` silently
returns the previous day's symbol with nothing looking broken.

## Where to go next

| If you want to | Go to |
|---|---|
| Install it and read your first record | [Getting Started](guides/getting-started.md) |
| Stream live market data | [Live Streaming](guides/live-streaming.md) |
| Run a live session inside ASP.NET Core or a worker | [Hosting and Dependency Injection](guides/hosting-and-dependency-injection.md) |
| Query historical data | [Historical Data](guides/historical-data.md) |
| Know what a `RecordRef` may outlive | [Zero-Copy and Allocation](guides/zero-copy-and-allocation.md) |
| Know why nothing here takes a `DateTime` | [Timestamps and Prices](guides/timestamps-and-prices.md) |
| Look up a type, member, or overload — each with an example | [API reference](api/index.md) |
| Work out why something is failing | [Troubleshooting](guides/troubleshooting.md) · [FAQ](guides/faq.md) |
| Run something end to end | [The five samples](https://github.com/jerbersoft/databentodotnet/tree/master/samples) |
| See what changed between versions | [Release notes](release-notes.md) |
| Contribute | [`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) |

**This site is the documentation.** The guides, the API reference and the release notes all live
here, and each fact lives in exactly one of them. That rule is not new — it is the one the wiki
used to state, and #82 kept it by moving the wiki's pages here and retiring the wiki rather than
by running two surfaces that would disagree within a release.

Repository conventions are the one thing deliberately not here. They bind a contributor at the
commit they are working on, which is an argument for living in the tree beside it, so they stay in
[`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md).

## Why the reference is complete

`GenerateDocumentationFile` and `TreatWarningsAsErrors` are both on for all five projects, so a
public member without a documentation comment has never compiled in this repository. There is no
undocumented corner to find, and a broken `<see cref>` is a build error rather than a bare word on
a page.

The same gate covers the examples. Every one on this site is a `<code>` block inside an
[XML documentation comment](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md),
which means it ships inside the NuGet package and reaches IntelliSense at the call site — the site
is a second rendering of it, not its home.
