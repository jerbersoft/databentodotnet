# DatabentoDotNet.Live

Real-time and intraday-replay streaming from [Databento](https://databento.com), over the raw TCP
gateway protocol. A .NET port of `databento-rs`'s live client.

> **0.9.1 is a beta.** The code is complete and tested; what is not yet settled is whether the
> public surface is the right shape. 1.0.0 undertakes not to break it, so the beta is when
> that undertaking is worth contesting — if something here is awkward to call,
> [an issue](https://github.com/jerbersoft/databentodotnet/issues) now is much cheaper than a
> major version later.

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;

await using var client = new LiveClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
    Dataset = "EQUS.MINI",
};

await client.SubscribeAsync(new Subscription
{
    Schema = Schema.Trades,
    Symbols = Symbols.From(["AAPL", "MSFT"]),
});

await client.StartAsync();

while (await client.FillBufferAsync() > 0)
{
    while (client.TryNextRecord(out RecordRef record))
    {
        if (record.TryGet(out TradeMsg trade))
            Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
    }
}
```

## Why it is a pair of calls and not one

Upstream's `next_record()` does not port, and cannot. An `async` method may not return a
`ref struct`, so there is no `Task<RecordRef>` — the split is `FillBufferAsync` (awaits bytes) and
`TryNextRecord` (reinterprets them in place, synchronously). That is the same lifetime rule the
decoder imposes, enforced by the compiler rather than by documentation: a `RecordRef` that tried to
survive an `await` fails to compile with CS4007.

Decoding allocates **nothing** per record, over a real socket — asserted in the test suite against a
mock gateway, not just reported by a benchmark. If you would rather have heap copies than manage that
lifetime, `RecordsAsync` gives you an `IAsyncEnumerable` and pays for it.

## Dependencies

[NodaTime](https://nodatime.org) — every timeout and `HeartbeatInterval` is a `Duration`, because
`TimeSpan` is banned repo-wide — plus `DatabentoDotNet.Dbn` and `ZstdSharp.Port`, the latter for a
session that negotiated `compression=zstd`.

## Documentation

The API reference is the XML documentation shipped inside this package. Guides, the reconnect and
`start_session` semantics, and troubleshooting are at
[jerbersoft.github.io/databentodotnet](https://jerbersoft.github.io/databentodotnet/) — start with
[Live Streaming](https://jerbersoft.github.io/databentodotnet/guides/live-streaming.html).

Source, issues, and roadmap: <https://github.com/jerbersoft/databentodotnet>.
