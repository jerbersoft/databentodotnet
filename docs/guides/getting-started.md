# Getting Started

**Install the packages, give them an API key, and read your first records.** Ten minutes, and the
last section prints live trades.

---

## Requirements

- **.NET 10 SDK or newer.** `net10.0` is the only target framework; there is no `netstandard2.0`
  and no .NET Framework support, and neither is planned. The codec is built on `ref struct`s,
  `[InlineArray]`, and static abstract interface members, none of which exist on the old runtime.
- **A Databento account and API key**, for anything live. Decoding `.dbn` files from disk needs
  no key at all.

Check your SDK:

```sh
dotnet --version      # 10.0.x or newer
```

## Adding the library to a project

```sh
dotnet add package DatabentoDotNet.Live
```

`DatabentoDotNet.Live` depends on `DatabentoDotNet.Dbn`, so that one line is enough if you only
stream — the codec comes with it. Add `DatabentoDotNet.Dbn` explicitly only if you never stream and
just read files. `DatabentoDotNet.Historical` and `DatabentoDotNet.Reference` are the other two.

**Pin the exact version.** `0.10.0` is a `0.x` release ([#102]) and the public API can still change
before 1.0:

```xml
<PackageReference Include="DatabentoDotNet.Live" Version="[0.10.0]" />
```

If the API is what you want changed, that is what the beta is for — say so on
[an issue](https://github.com/jerbersoft/databentodotnet/issues) rather than working around it.

Two runtime dependencies come with the codec, and both are deliberate:
[`ZstdSharp.Port`](https://www.nuget.org/packages/ZstdSharp.Port) for DBN's Zstandard transport
compression, and [`NodaTime`](https://nodatime.org) for all date and time handling — a BCL
`DateTime` tick is 100 ns and cannot represent a DBN timestamp at all. `ZstdSharp.Port` is pure
managed — no P/Invoke, no native asset, no per-RID build — so neither dependency stops you
publishing trimmed or Native AOT. The two HTTP clients add
`Microsoft.Extensions.Logging.Abstractions` for their optional `LoggerFactory`.

### Building from source instead

Only needed if you want to change the library or run its test suite:

```sh
git clone https://github.com/jerbersoft/databentodotnet.git
```

```xml
<ItemGroup>
  <ProjectReference Include="../databentodotnet/src/DatabentoDotNet.Live/DatabentoDotNet.Live.csproj" />
</ItemGroup>
```

### Three namespaces, and one that surprises people

```csharp
using DatabentoDotNet;          // ApiKey, Symbols, UserAgent — shared by every client
using DatabentoDotNet.Dbn;      // the codec: records, Metadata, DbnDecoder, DbnTime, enums
using DatabentoDotNet.Live;     // LiveClient, Subscription, LiveGateway
```

`ApiKey` and `Symbols` are in the **root** `DatabentoDotNet` namespace rather than under `.Dbn`,
because they are common to the live and historical clients both. They ship in the
`DatabentoDotNet.Dbn` package, so the package reference is the same — only the `using` differs.

Build and run the test suite to confirm the clone is sound:

```sh
cd databentodotnet
dotnet build
dotnet test            # the live-gateway tests skip themselves without an API key
```

## Your API key

An API key is 32 characters and starts with `db-`. Get one from the
[Databento portal](https://databento.com/portal/keys).

**Keep it out of source.** The repository's own tests read it from the environment, and the
`.env` file that supplies it is git-ignored:

```sh
export DATABENTO_API_KEY='db-...'
```

```csharp
using DatabentoDotNet;      // ApiKey and Symbols live in the root namespace

var key = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!);
```

`ApiKey` validates length and shape at construction, so a truncated or pasted-with-whitespace key
fails immediately rather than as a puzzling `success=0` from the gateway. Its `ToString()`
deliberately renders as `…a1b2c` — the last five characters only, the bucket ID — so a key cannot
leak through a log line, an exception message, or a debugger watch window.

## Decoding a file — no key required

The fastest way to see the codec work. Any `.dbn` or `.dbn.zst` file will do; the repository
vendors 71 of them under `tests/DatabentoDotNet.Dbn.Tests/Data/`.

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("trades.dbn.zst"));   // zstd is detected, not declared

Console.WriteLine($"dataset {decoder.Metadata!.Dataset}, DBN v{decoder.Metadata.Version}");

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
    {
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)}  {trade.Price / 1e9:F4} x {trade.Size}");
    }
}
```

Three things in that loop are worth knowing before you write your own:

- **`IndexTs`, not `Header.TsEvent`.** Most schemas index on `ts_recv`, and the two can fall on
  opposite sides of UTC midnight. [Timestamps and Prices](timestamps-and-prices.md) explains what
  goes wrong when you pick the other one.
- **`Price / 1e9`.** Prices are `long` at a fixed 1e-9 scale. That division is fine for display
  and wrong for arithmetic — same page.
- **`record` is valid only until the next `TryNextRecord`.** It is a `ref struct` pointing into
  the decoder's buffer. The compiler stops you storing it; [Zero-Copy and
  Allocation](zero-copy-and-allocation.md) explains what it is protecting you from.

More on files: [Decoding DBN Files](decoding-dbn-files.md).

## Streaming live

Five calls, in this order. **`StartAsync` is where billing begins** — everything before it is
free, and the gateway sends no market data at all until it is called.

```csharp
using DatabentoDotNet;          // ApiKey, Symbols
using DatabentoDotNet.Dbn;      // records, Metadata, DbnTime, Schema
using DatabentoDotNet.Live;     // LiveClient, Subscription

await using var client = new LiveClient
{
    ApiKey  = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
    Dataset = "EQUS.MINI",
};

await client.ConnectAsync();
await client.AuthenticateAsync();
await client.SubscribeAsync(new Subscription
{
    Schema  = Schema.Trades,
    Symbols = Symbols.From(["AAPL", "MSFT"]),
});

Metadata metadata = await client.StartAsync();      // billing starts here

await foreach (var record in client.RecordsAsync())
{
    if (record.TryGet(out TradeMsg trade))
    {
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)}  {trade.Price / 1e9:F4} x {trade.Size}");
    }
}
```

`RecordsAsync` is the convenient surface and it copies each record onto the heap — a `ref struct`
cannot cross a `yield return` any more than it can cross an `await`. For the zero-allocation loop,
and for everything else about the session, see [Live Streaming](live-streaming.md).

**A live data license is separate from historical access.** An account with full historical
entitlements is still refused on a dataset it has not licensed for *live*, with
`success=0|error=A live data license is required to access XNAS.ITCH.` See
[Troubleshooting](troubleshooting.md).

## Where to go next

- [Live Streaming](live-streaming.md) — subscriptions, reconnection, heartbeats, and the two record loops
- [Hosting and Dependency Injection](hosting-and-dependency-injection.md) — the same session as a hosted service in ASP.NET Core or a worker, configured from `appsettings.json` rather than in code
- [Timestamps and Prices](timestamps-and-prices.md) — read this before you compute anything from a record
- [Symbol Resolution](symbol-resolution.md) — `instrument_id` back to a ticker
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — the guarantee, and the rules that hold it up

[#102]: https://github.com/jerbersoft/databentodotnet/issues/102
