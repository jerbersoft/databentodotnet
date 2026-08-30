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
    <a class="btn btn-primary btn-lg" href="api/index.md">API reference</a>
    <a class="btn btn-outline-secondary btn-lg" href="https://github.com/jerbersoft/databentodotnet/wiki">Guides</a>
    <a class="btn btn-outline-secondary btn-lg" href="https://github.com/jerbersoft/databentodotnet">GitHub</a>
  </p>
</div>

> [!NOTE]
> This is a third-party client. It is not published or endorsed by Databento, who ship official
> clients for Python, C++ and Rust. **0.9.0 is a beta**: the code is complete and tested, and what
> is not yet settled is whether the public surface is the right shape.

## Install

Four packages, and you take only the ones you use. `DatabentoDotNet.Dbn` is the only one with no
sibling dependency; each of the other three brings it in.

```sh
dotnet add package DatabentoDotNet.Dbn          # the DBN codec
dotnet add package DatabentoDotNet.Live         # real-time streaming
dotnet add package DatabentoDotNet.Historical   # historical HTTPS API
dotnet add package DatabentoDotNet.Reference    # security master, corporate actions
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
| Look up a type, member, or overload — each with an example | [API reference](api/index.md) |
| Learn the library, or understand a design decision | [The wiki](https://github.com/jerbersoft/databentodotnet/wiki) |
| Know what a `RecordRef` may outlive | [Zero-Copy and Allocation](https://github.com/jerbersoft/databentodotnet/wiki/Zero-Copy-and-Allocation) |
| Know why nothing here takes a `DateTime` | [Timestamps and Prices](https://github.com/jerbersoft/databentodotnet/wiki/Timestamps-and-Prices) |
| Run something end to end | [The four samples](https://github.com/jerbersoft/databentodotnet/tree/master/samples) |
| Contribute | [`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) |

**This site is the API reference and deliberately nothing else.** Guides, explanations and
troubleshooting live in the [wiki](https://github.com/jerbersoft/databentodotnet/wiki); repository
conventions live in
[`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md). The wiki's own
[style guide](https://github.com/jerbersoft/databentodotnet/wiki/Wiki-Style-Guide) draws that line
and gives the reason: one canonical location per fact, because the second copy is the one that goes
stale.

## The four packages

| Package | Contents |
|---|---|
| `DatabentoDotNet.Dbn` | The DBN codec — record structs, metadata, decoder, symbol maps |
| `DatabentoDotNet.Live` | Real-time and intraday-replay streaming over the raw TCP gateway |
| `DatabentoDotNet.Historical` | Historical HTTPS API — timeseries, batch, symbology, metadata |
| `DatabentoDotNet.Reference` | Security master, corporate actions, adjustment factors |

## Why the reference is complete

`GenerateDocumentationFile` and `TreatWarningsAsErrors` are both on for all four projects, so a
public member without a documentation comment has never compiled in this repository. There is no
undocumented corner to find, and a broken `<see cref>` is a build error rather than a bare word on
a page.

The same gate covers the examples. Every one on this site is a `<code>` block inside an
[XML documentation comment](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md),
which means it ships inside the NuGet package and reaches IntelliSense at the call site — the site
is a second rendering of it, not its home.
