// Hosting and dependency injection: a live session run as a BackgroundService inside the .NET
// generic host, configured almost entirely from appsettings.json rather than in code.
//
//   export DATABENTO_API_KEY=db-...
//   dotnet run --project samples/DatabentoDotNet.Samples.HostedLive
//
// appsettings.json in this directory carries the session — dataset, schema, symbols, reconnection
// — and deliberately carries no API key. The key comes from DATABENTO_API_KEY and nothing else,
// which is the environment-variable check below and the last leg of the precedence chain
// LiveSessionResolver implements: a session's own ApiKey, then Databento:ApiKey, then this
// variable. See the "Hosting and Dependency Injection" guide for the rest of the configuration
// shape this file only shows one corner of.
//
// THIS COSTS MONEY. A live session bills for the data it delivers, from `start_session` onward.
// Everything before that — connecting, authenticating, subscribing — is free, which is why the
// banner below prints before host.RunAsync() starts the session rather than after.

using System.Globalization;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var key = Environment.GetEnvironmentVariable("DATABENTO_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    // The environment, and only the environment. There is a .env file at the root of this
    // repository and the test projects read it; that is harness machinery, and a sample that
    // copied it — or that put a key in appsettings.json — would teach a reader to keep credentials
    // in their source tree.
    Console.Error.WriteLine("DATABENTO_API_KEY is not set. Export your key and run this again:");
    Console.Error.WriteLine();
    Console.Error.WriteLine("    export DATABENTO_API_KEY=db-...");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// AddDatabento binds the common Databento section. This sample leaves Databento:ApiKey unset in
// appsettings.json, so the environment-variable check above is what actually supplies one — the
// point of this sample is that last leg of the precedence chain, not the first two.
builder.Services.AddDatabento();

// Everything about the "equities" session — dataset, subscriptions, reconnection policy — comes
// from Databento:Live:equities in appsettings.json. AddRecordHandler is the one call still made
// in code: sessions are declared here, never conjured from a configuration key with no handler
// behind it.
builder.Services.AddDatabentoLive("equities").AddRecordHandler<TradePrinter>();

using var host = builder.Build();

Console.WriteLine("This sample starts a hosted live session, and a live session BILLS FOR THE DATA");
Console.WriteLine($"IT DELIVERS. It stops after {TradePrinter.MaxRecords} records or on Ctrl+C, whichever");
Console.WriteLine("comes first — the host's own Console lifetime handles Ctrl+C, so this sample does");
Console.WriteLine("not have to.");
Console.WriteLine();

// Connecting, authenticating, subscribing and starting all happen inside RunAsync, before it
// returns control here — see LiveSessionService.StartAsync's own remarks for why that ordering
// matters. RunAsync then blocks until TradePrinter asks the host to stop, or until Ctrl+C does.
await host.RunAsync();

var runner = host.Services.GetRequiredKeyedService<LiveSessionRunner>("equities");
Console.WriteLine();
Console.WriteLine($"{runner.RecordsReceived} record(s) received; final state {runner.State}.");

return 0;

// A singleton, registered once per session by AddRecordHandler<THandler>() — not one instance per
// record, which a DI scope per record would otherwise invite and which would allocate in the one
// package whose reason to exist is that it does not. See ILiveRecordHandler's own documentation
// for the two-method split this interface has instead of an async, per-record callback.
internal sealed class TradePrinter(IHostApplicationLifetime lifetime) : ILiveRecordHandler
{
    // Enough records to see the shape of the stream and few enough to stop on its own. A sample
    // that runs until interrupted is a sample somebody leaves running.
    public const int MaxRecords = 20;

    private int _count;

    public void OnRecord(scoped RecordRef record)
    {
        // Copy out what you need. The RecordRef points into the runner's read buffer and is valid
        // for this call only — the next fill may shift it.
        if (record.TryGet(out TradeMsg trade))
        {
            var price = trade.Price == DbnConstants.UndefPrice
                ? "—"
                : ((decimal)trade.Price / DbnConstants.FixedPriceScale).ToString(
                    "F4", CultureInfo.InvariantCulture);

            Console.WriteLine($"instrument {record.Header.InstrumentId,10}  {price,12} x {trade.Size,-8}");
        }

        if (++_count >= MaxRecords)
        {
            // Ask the host to stop from inside the handler. OnRecord cannot await anything — an
            // async method cannot take a ref struct across an await — so this only requests the
            // shutdown; LiveSessionService.StopAsync is what actually half-closes the session.
            lifetime.StopApplication();
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
