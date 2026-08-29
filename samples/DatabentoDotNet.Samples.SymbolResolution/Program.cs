// Symbol resolution, and the mapping applied to real records.
//
//   export DATABENTO_API_KEY=db-...
//   dotnet run --project samples/DatabentoDotNet.Samples.SymbolResolution -- [dataset] [symbols] [schema] [date]
//
// Defaults: GLBX.MDP3, ESH4,ESM4, trades, 2024-01-02 — two expired contracts, so two instrument ids
// to tell apart.
//
// `symbology.resolve` is free. The download at the end is not: records are what a symbol map is
// *for*, and a mapping printed on its own does not show the part that matters, which is that DBN
// records carry an instrument id and nothing else. So this sample prices the download and refuses
// to spend more than a ceiling it names, the same way
// samples/DatabentoDotNet.Samples.HistoricalRange does.

using System.Globalization;
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime.Text;

const decimal CostCeilingUsd = 0.01m;
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
var symbols = (Arg(1) ?? "ESH4,ESM4").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
    DateTimeRange = DateRange.OnDay(parsedDate.Value).ToDateTimeRange(),
    Limit = MaxRecords,

    // Ask for raw symbols in and instrument ids out — which is what the records will carry, and
    // therefore what has to be mapped back.
    StypeIn = SType.RawSymbol,
    StypeOut = SType.InstrumentId,
};

Console.WriteLine($"dataset          {request.Dataset}");
Console.WriteLine($"symbols          {request.Symbols.ToApiString()}");
Console.WriteLine($"date             {LocalDatePattern.Iso.Format(parsedDate.Value)}");
Console.WriteLine();

// FromQuery derives the resolution from the range request rather than restating it, so the mapping
// covers exactly the window the records come from. Resolving a different window than you download
// is the mistake this overload exists to prevent.
var resolution = await client.Symbology.ResolveAsync(ResolveParams.FromQuery(request));

Console.WriteLine($"resolved         {resolution.StypeIn.ToWireString()} -> {resolution.StypeOut.ToWireString()}");
Console.WriteLine();

foreach (var (rawSymbol, intervals) in resolution.Mappings)
{
    foreach (var interval in intervals)
    {
        // Half-open: the mapping holds from StartDate up to but not including EndDate. An
        // instrument id is only unique within its interval, which is why the map is keyed by date
        // as well as by id.
        Console.WriteLine($"  {rawSymbol,-10} {LocalDatePattern.Iso.Format(interval.StartDate)} .. {LocalDatePattern.Iso.Format(interval.EndDate)}  -> {interval.Symbol}");
    }
}

// A symbol the dataset does not know, or knows for only part of the window, is reported rather than
// thrown. Checking both is the difference between a resolution and an assumption.
if (resolution.NotFound.Count > 0)
{
    Console.WriteLine($"  not found      {string.Join(", ", resolution.NotFound)}");
}

if (resolution.Partial.Count > 0)
{
    Console.WriteLine($"  partial        {string.Join(", ", resolution.Partial)}");
}

Console.WriteLine();

// A time-series map: instrument id plus date to symbol, because ids are reused across days.
var map = resolution.ToSymbolMap();

Console.WriteLine($"symbol map       {map.Count} entries");
Console.WriteLine();

var quoted = await client.Metadata.GetCostAsync(request.ToQuery());
Console.WriteLine($"cost             ${quoted.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine();

if (quoted > CostCeilingUsd)
{
    Console.Error.WriteLine(
        $"That is more than the ${CostCeilingUsd.ToString(CultureInfo.InvariantCulture)} this sample "
        + "will spend, so nothing was downloaded. The resolution above cost nothing and stands.");
    return 3;
}

await using var reader = await client.Timeseries.GetRangeAsync(request);

// The metadata block of this response carries the same mappings, so TsSymbolMap.FromMetadata(
// reader.Metadata) is the shortcut when a download is happening anyway. symbology.resolve is what
// answers the question *without* one — before deciding whether to buy, or for a stream that has no
// metadata block of its own.
var count = 0;
await foreach (var record in reader.ReadRecordsAsync())
{
    var when = DbnTime.TryToInstant(record.IndexTs, out var instant)
        ? InstantPattern.ExtendedIso.Format(instant)
        : "(no timestamp)";

    // The lookup takes the record itself: it reads the instrument id and the date off the record's
    // own timestamp, which is the pair the map is keyed by.
    var symbol = map.TryGetSymbol(record.AsRef(), out var resolved) ? resolved : "(unmapped)";

    var line = $"{when,-30}  instrument {record.Header.InstrumentId,10}  {symbol,-10}";

    if (record.TryGet<TradeMsg>(out var trade))
    {
        var price = trade.Price == DbnConstants.UndefPrice
            ? "        —"
            : ((decimal)trade.Price / DbnConstants.FixedPriceScale).ToString("F4", CultureInfo.InvariantCulture);

        line += $"  {price,12} x {trade.Size,-8} {trade.ActionChar}{trade.SideChar}";
    }

    Console.WriteLine(line);
    count++;
}

Console.WriteLine();
Console.WriteLine($"{count} record(s) mapped");

return 0;

string? Arg(int index) => args.Length > index && args[index].Length > 0 ? args[index] : null;
