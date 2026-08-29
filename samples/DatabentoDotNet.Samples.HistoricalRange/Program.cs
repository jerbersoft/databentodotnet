// A historical range, priced before it is taken.
//
//   export DATABENTO_API_KEY=db-...
//   dotnet run --project samples/DatabentoDotNet.Samples.HistoricalRange -- [dataset] [symbols] [schema] [date]
//
// Defaults: GLBX.MDP3, ESH4, trades, 2024-01-02 — an expired contract on a settled day, so the
// answer does not move with the calendar.
//
// THIS COSTS MONEY, and that is the point of the shape below. `metadata.get_cost` and
// `metadata.get_billable_size` are free: they answer what a request *would* cost without making it.
// So this sample asks the price first and refuses to spend more than a ceiling it names out loud.
// Printing a cost and then downloading regardless would teach the habit without the part that makes
// it a habit.

using System.Globalization;
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;
using NodaTime.Text;

// The most this sample will spend in one run, in USD. The default query below prices at a few
// millionths of a cent; the ceiling is here so that widening the range in the arguments fails
// loudly rather than silently costing dollars.
const decimal CostCeilingUsd = 0.01m;

// Small enough to print. `limit` is part of the priced query, so it lowers the bill as well as the
// output — which is why it is set on the request rather than by breaking out of the loop.
const ulong MaxRecords = 10;

var key = Environment.GetEnvironmentVariable("DATABENTO_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("DATABENTO_API_KEY is not set. Export your key and run this again:");
    Console.Error.WriteLine();
    Console.Error.WriteLine("    export DATABENTO_API_KEY=db-...");
    return 1;
}

var dataset = Arg(0) ?? "GLBX.MDP3";
var symbols = (Arg(1) ?? "ESH4").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (!WireStrings.TryParseSchema(Arg(2) ?? "trades", out var schema))
{
    Console.Error.WriteLine($"'{Arg(2)}' is not a DBN schema. Use its wire spelling: trades, mbp-1, ohlcv-1d, definition.");
    return 2;
}

var parsedDate = LocalDatePattern.Iso.Parse(Arg(3) ?? "2024-01-02");
if (!parsedDate.Success)
{
    Console.Error.WriteLine($"'{Arg(3)}' is not an ISO yyyy-MM-dd date.");
    return 2;
}

await using var client = new HistoricalClient { ApiKey = new ApiKey(key) };

var request = new GetRangeParams
{
    Dataset = dataset,
    Symbols = Symbols.From(symbols),
    Schema = schema,

    // One UTC day. DateRange is half-open — OnDay covers midnight to the next midnight — and
    // ToDateTimeRange widens it to the nanosecond instants the timeseries endpoint takes.
    DateTimeRange = DateRange.OnDay(parsedDate.Value).ToDateTimeRange(),
    Limit = MaxRecords,
};

Console.WriteLine($"dataset          {request.Dataset}");
Console.WriteLine($"symbols          {request.Symbols.ToApiString()}");
Console.WriteLine($"schema           {request.Schema.ToWireString()}");
Console.WriteLine($"range            {InstantPattern.ExtendedIso.Format(request.DateTimeRange.Start)} .. {InstantPattern.ExtendedIso.Format(request.DateTimeRange.End)}");
Console.WriteLine($"limit            {request.Limit}");
Console.WriteLine();

// ToQuery() turns the request into exactly the question the pricing endpoints answer, so the price
// quoted is for the request that follows rather than for one assembled a second time by hand.
var query = request.ToQuery();
var cost = await client.Metadata.GetCostAsync(query);
var billableBytes = await client.Metadata.GetBillableSizeAsync(query);

Console.WriteLine($"billable size    {billableBytes} bytes");
Console.WriteLine($"cost             ${cost.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine();

if (cost > CostCeilingUsd)
{
    Console.Error.WriteLine(
        $"That is more than the ${CostCeilingUsd.ToString(CultureInfo.InvariantCulture)} this sample "
        + "will spend, so nothing was downloaded. Narrow the range, or raise CostCeilingUsd if you "
        + "meant it.");
    return 3;
}

await using var reader = await client.Timeseries.GetRangeAsync(request);

// Every DBN stream opens with a metadata block. It echoes the request rather than describing the
// answer, so it says what was asked for even when nothing came back.
Console.WriteLine($"DBN v{reader.Metadata.Version}, stype_out {reader.Metadata.StypeOut.ToWireString()}");
Console.WriteLine();

var count = 0;
await foreach (var record in reader.ReadRecordsAsync())
{
    // ReadRecordsAsync copies each record out of the buffer, which is the readable path and the
    // right one here: this sample holds records across awaits. The zero-copy pair —
    // FillBufferAsync and TryNextRecord — is on TimeseriesReader too, and
    // samples/DatabentoDotNet.Samples.LiveStream shows what it looks like.
    var when = DbnTime.TryToInstant(record.IndexTs, out var instant)
        ? InstantPattern.ExtendedIso.Format(instant)
        : "(no timestamp)";

    // RType is the wire's own name for the record shape, which is not always the schema's name for
    // it: a trade is rtype Mbp0 — market-by-price carrying zero book levels.
    var line = $"{when,-30}  {record.Header.RType,-16}  instrument {record.Header.InstrumentId,10}";

    if (record.TryGet<TradeMsg>(out var trade))
    {
        // Prices are fixed-point integers scaled by 1e9, not floating point. Dividing through
        // decimal keeps the exact value; dividing through double would not.
        var price = trade.Price == DbnConstants.UndefPrice
            ? "        —"
            : ((decimal)trade.Price / DbnConstants.FixedPriceScale).ToString("F4", CultureInfo.InvariantCulture);

        line += $"  {price,12} x {trade.Size,-8} {trade.ActionChar}{trade.SideChar}";
    }

    Console.WriteLine(line);
    count++;
}

Console.WriteLine();
Console.WriteLine($"{count} record(s), ${cost.ToString(CultureInfo.InvariantCulture)} spent");

return 0;

string? Arg(int index) => args.Length > index && args[index].Length > 0 ? args[index] : null;
