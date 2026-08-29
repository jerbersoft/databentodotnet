// Live streaming, end to end: connect, authenticate, subscribe, read records, stop cleanly.
//
//   export DATABENTO_API_KEY=db-...
//   dotnet run --project samples/DatabentoDotNet.Samples.LiveStream -- [dataset] [schema] [symbols] [replay-minutes]
//
// Defaults: EQUS.MINI, trades, AAPL,MSFT,NVDA, and no replay.
//
// `replay-minutes` asks the gateway to begin that many minutes in the past instead of at the live
// edge. Outside market hours a live subscription is silent and this sample would report nothing, so
// pass something like 1400 on a weekend to replay the last session and watch records arrive.
//
// THIS COSTS MONEY. A live session bills for the data it delivers, from `start_session` onward.
// Everything before that line — connecting, authenticating, subscribing — is free, which is why the
// banner below prints before StartAsync rather than after it.

using System.Globalization;
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Live;
using NodaTime;
using NodaTime.Text;

// Enough records to see the shape of the stream and few enough to stop on its own. A sample that
// runs until interrupted is a sample somebody leaves running.
const int MaxRecords = 20;

var key = Environment.GetEnvironmentVariable("DATABENTO_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    // The environment, and only the environment. There is a .env file at the root of this
    // repository and the test projects read it; that is harness machinery, and a sample that
    // copied it would teach a reader to keep credentials in their source tree.
    Console.Error.WriteLine("DATABENTO_API_KEY is not set. Export your key and run this again:");
    Console.Error.WriteLine();
    Console.Error.WriteLine("    export DATABENTO_API_KEY=db-...");
    return 1;
}

var dataset = Arg(0) ?? "EQUS.MINI";
var symbols = (Arg(2) ?? "AAPL,MSFT,NVDA").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (!WireStrings.TryParseSchema(Arg(1) ?? "trades", out var schema))
{
    Console.Error.WriteLine($"'{Arg(1)}' is not a DBN schema. Use its wire spelling: trades, mbp-1, ohlcv-1m, definition.");
    return 2;
}

var replayMinutes = 0;
if (Arg(3) is { } replayText && !int.TryParse(replayText, CultureInfo.InvariantCulture, out replayMinutes))
{
    Console.Error.WriteLine($"'{replayText}' is not a whole number of minutes.");
    return 2;
}

// Instant and Duration rather than DateTime and TimeSpan. A DBN timestamp is nanoseconds and a
// DateTime tick is 100 of them, so the BCL types cannot represent one — see CLAUDE.md.
Instant? replayFrom = replayMinutes > 0
    ? SystemClock.Instance.GetCurrentInstant() - Duration.FromMinutes(replayMinutes)
    : null;

Console.WriteLine($"dataset          {dataset}");
Console.WriteLine($"schema           {schema.ToWireString()}");
Console.WriteLine($"symbols          {string.Join(", ", symbols)}");
Console.WriteLine($"start            {(replayFrom is { } from ? InstantPattern.ExtendedIso.Format(from) : "live edge")}");
Console.WriteLine();
Console.WriteLine("This sample starts a live session, and a live session BILLS FOR THE DATA IT");
Console.WriteLine($"DELIVERS. It stops after {MaxRecords} records or on Ctrl+C, whichever comes first.");
Console.WriteLine();

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    // Cancel the token rather than letting the runtime kill the process, so the session is closed
    // on the way out instead of abandoned.
    eventArgs.Cancel = true;
    stopping.Cancel();
};

await using var client = new LiveClient
{
    ApiKey = new ApiKey(key),
    Dataset = dataset,

    // Gateway is left unset: the client derives lsg.databento.com's host for this dataset itself.
};

// Four steps, and they are separate because the protocol is. Nothing is billed until StartAsync.
await client.ConnectAsync(stopping.Token);
await client.AuthenticateAsync(stopping.Token);

var subscription = await client.SubscribeAsync(
    new Subscription
    {
        Schema = schema,
        StypeIn = SType.RawSymbol,
        Symbols = Symbols.From(symbols),
        Start = replayFrom,
    },
    stopping.Token);

Console.WriteLine($"subscribed       id {subscription.Id}");

var metadata = await client.StartAsync(stopping.Token);

Console.WriteLine($"session started  DBN v{metadata.Version}, stype_out {metadata.StypeOut.ToWireString()}");
Console.WriteLine();

var count = 0;
try
{
    while (count < MaxRecords)
    {
        // Drain what the last read already produced before asking the socket for more.
        //
        // This pair is the whole reason the live sample exists. There is no Task<RecordRef> in this
        // library and there never can be: an async method cannot return a ref struct, and RecordRef
        // is one because it points *into* the read buffer rather than copying out of it. So the
        // await and the reinterpret are two calls, and a record never survives an await. See
        // PORTING.md §1.
        while (client.TryNextRecord(out var record))
        {
            Console.WriteLine(Describe(record));

            if (++count >= MaxRecords)
            {
                break;
            }
        }

        if (count >= MaxRecords)
        {
            break;
        }

        if (await client.FillBufferAsync(stopping.Token) == 0)
        {
            Console.WriteLine("gateway closed the stream");
            break;
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("interrupted");
}

// The clean stop: half-close the socket and let the gateway finish, rather than dropping it.
await client.CloseAsync();

Console.WriteLine();
Console.WriteLine($"{count} record(s) read");

if (count == 0)
{
    Console.WriteLine();
    Console.WriteLine("No records arrived. A live subscription is silent when the market is closed —");
    Console.WriteLine("pass a replay-minutes argument to start in the past instead of at the live edge.");
}

return 0;

// Non-static so it can read `args`, which top-level statements hand to this file as a parameter.
string? Arg(int index) => args.Length > index && args[index].Length > 0 ? args[index] : null;

static string Describe(RecordRef record)
{
    var when = DbnTime.TryToInstant(record.IndexTs, out var instant)
        ? InstantPattern.ExtendedIso.Format(instant)
        : "(no timestamp)";

    var line = $"{when,-30}  {record.Header.RType,-16}  instrument {record.Header.InstrumentId,10}";

    // Get<T>() reinterprets the buffer in place and TryGet<T>() checks the rtype first, so this
    // costs a bounds check rather than a copy. Every schema has its own struct; trades is the one
    // this sample subscribes to by default.
    if (record.TryGet<TradeMsg>(out var trade))
    {
        var price = trade.Price == DbnConstants.UndefPrice
            ? "        —"
            : ((decimal)trade.Price / DbnConstants.FixedPriceScale).ToString("F4", CultureInfo.InvariantCulture);

        line += $"  {price,12} x {trade.Size,-8} {trade.ActionChar}{trade.SideChar}";
    }

    // Symbol mappings arrive interleaved with the data, which is how a live consumer learns what
    // an instrument id means. samples/DatabentoDotNet.Samples.SymbolResolution does that lookup.
    if (record.TryGet<SymbolMappingMsg>(out var mapping))
    {
        line += $"  {mapping.StypeInSymbol} -> {mapping.StypeOutSymbol}";
    }

    return line;
}
