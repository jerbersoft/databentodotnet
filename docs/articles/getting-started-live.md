# Getting started: `DatabentoDotNet.Live`

Real-time and intraday-replay streaming, over Databento's raw TCP gateway protocol.

```sh
dotnet add package DatabentoDotNet.Live
```

Brings in `DatabentoDotNet.Dbn`. Read [the zero-copy contract](zero-copy.md) first if you have not
— the live read loop is where its rules are load-bearing rather than academic.

> [!WARNING]
> **A live session bills for the data it delivers.** Connecting, authenticating and subscribing are
> free; `StartAsync` is the line where billing begins. Everything below is written so that line is
> visible.

## A session, end to end

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;

await using var client = new LiveClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
    Dataset = "EQUS.MINI",

    // Gateway left unset: the client derives the right lsg.databento.com host for this dataset.
};

await client.ConnectAsync();
await client.AuthenticateAsync();

var subscription = await client.SubscribeAsync(new Subscription
{
    Schema = Schema.Trades,
    StypeIn = SType.RawSymbol,
    Symbols = Symbols.From(["AAPL", "MSFT", "NVDA"]),
});

// Nothing is billed before this line.
Metadata metadata = await client.StartAsync();

while (true)
{
    while (client.TryNextRecord(out var record))
    {
        if (record.TryGet<TradeMsg>(out var trade))
        {
            Console.WriteLine($"{DbnTime.ToInstant(record.IndexTs)}  {trade.Price} x {trade.Size}");
        }
    }

    if (await client.FillBufferAsync() == 0)
    {
        break;   // the gateway closed the stream
    }
}

// Half-close and let the gateway finish, rather than dropping the socket.
await client.CloseAsync();
```

`ApiKey` and `Dataset` are `required` init properties, so the compiler will not let you construct a
client without them. Everything else has a default.

## The four steps are separate because the protocol is

`ConnectAsync` → `AuthenticateAsync` → `SubscribeAsync` → `StartAsync` is not ceremony that could be
folded into one call. The gateway's own protocol has these as distinct exchanges, and keeping them
apart buys three things a combined `OpenAsync` would take away:

- **Subscriptions are made before the session starts.** You can add several, and the data does not
  begin flowing — and billing does not begin — until `StartAsync`.
- **The expensive step is visible.** A reader of your code can see where money starts being spent.
- **Failures are attributable.** A bad key fails at `AuthenticateAsync`, an unknown symbol at
  `SubscribeAsync`, and neither is confusable with a network problem at `ConnectAsync`.

`StartAsync` returns the stream's <xref:DatabentoDotNet.Dbn.Metadata>, which is where `StypeOut`,
the DBN version, and the symbols the gateway could *not* resolve (`NotFound`) come from. Check
`NotFound` — a misspelled symbol is silence, not an error.

## Drain, then fill

The read loop above is the shape, and it is not a stylistic choice. `RecordRef` is a `ref struct`,
so it cannot cross an `await`; an `async` method cannot return one either, which is why there is no
`Task<RecordRef>` and never can be. So the two halves are separate calls:

- `TryNextRecord` is **synchronous** and hands you a record pointing into the buffer.
- `FillBufferAsync` is **asynchronous** and refills that buffer, returning the byte count — `0`
  means the gateway closed the stream.

Drain everything the last read produced, *then* go back to the socket. This path allocates exactly
zero bytes per record, and that is asserted on every `dotnet test` rather than merely benchmarked.

If you would rather have objects — and for most code you would — `RecordsAsync` gives you the same
stream as `IAsyncEnumerable<OwnedRecord>`:

```csharp
await foreach (var record in client.RecordsAsync(cancellationToken))
{
    ...
}
```

That allocates one `OwnedRecord` per record and has none of the lifetime rules. Start there; move
to the zero-allocation pair when a profile tells you to.

## Replay: the market is closed more than it is open

A live subscription is **silent** outside market hours. This is the single most common "nothing
happens and nothing is wrong" experience with this package, and the fix is `Subscription.Start`:

```csharp
using NodaTime;

await client.SubscribeAsync(new Subscription
{
    Schema = Schema.Trades,
    StypeIn = SType.RawSymbol,
    Symbols = Symbols.From(["AAPL"]),

    // Begin ~24 hours in the past instead of at the live edge, and catch up from there.
    Start = SystemClock.Instance.GetCurrentInstant() - Duration.FromHours(24),
});
```

`Start` is an `Instant?`. Left null, the session begins at the live edge. Set, the gateway replays
from that point and then continues live — so the same loop reads both without knowing which it is
getting. `Duration` and `Instant`, not `TimeSpan` and `DateTime`; see [Time](time.md).

`UseSnapshot` is the related knob for book schemas: it asks for the current state of the book
before the incremental updates, so you are not reconstructing it from an arbitrary mid-session
starting point.

## Session options worth knowing

| Property | What it is for |
|---|---|
| `Compression` | `Compression.Zstd` negotiates compressed transport on the authentication line |
| `SendTsOut` | Adds the gateway's send timestamp to every record — the far end of a latency measurement |
| `HeartbeatInterval` | How often the gateway sends a heartbeat on an idle subscription |
| `ConnectTimeout`, `AuthTimeout`, `ReadTimeout` | All `Duration`; `EffectiveReadTimeout` reports what is actually in force |
| `SlowReaderBehavior` | `Warn` or `Skip`, for when your loop cannot keep up with the gateway |

`SlowReaderBehavior` deserves a moment. The gateway buffers for a consumer that falls behind, and
then stops being willing to. `Warn` tells you; `Skip` drops data to keep the session alive. Neither
is the safe default for every application, which is why it is nullable and unset by default — the
gateway's own behaviour applies unless you choose.

## Reconnecting

```csharp
await client.ReconnectAsync();      // connect + authenticate again
await client.ResubscribeAsync();    // replay the subscriptions this client already made
await client.StartAsync();
```

The client remembers its subscriptions, so `ResubscribeAsync` re-sends them without you rebuilding
the list. Note that this starts a **new billable session** — reconnecting in a loop against a
gateway that keeps dropping you is a way to spend money quickly.

## Where to go next

- [The zero-copy contract](zero-copy.md) — the read loop's rules, and when to use `OwnedRecord`.
- [`samples/DatabentoDotNet.Samples.LiveStream`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.LiveStream) —
  everything above as one runnable file, with a replay argument and a clean Ctrl+C.
- [Testing conventions](testing.md) — how this repository tests against a real gateway without
  spending money by accident.
- <xref:DatabentoDotNet.Live.LiveClient>, <xref:DatabentoDotNet.Live.Subscription> in the API
  reference.
