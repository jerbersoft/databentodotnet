// A batch job, end to end: submit, poll, download, read.
//
//   export DATABENTO_API_KEY=db-...
//   dotnet run --project samples/DatabentoDotNet.Samples.BatchDownload -- [dataset] [symbols] [schema] [date] [output-directory]
//
// Defaults: GLBX.MDP3, ESH4, trades, 2024-01-02, and a directory under the system temp path.
//
// THIS COSTS MONEY. Submitting the job is the billable act — everything after it (polling, listing,
// downloading) is free, and the files stay available until the job expires. So the price is asked
// before the submit, and the job id is printed the moment it exists: if this sample is interrupted
// while polling, the data is already paid for and can be collected with that id rather than by
// submitting a second job.

using System.Globalization;
using DatabentoDotNet;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;
using NodaTime;
using NodaTime.Text;

// The most this sample will spend in one run, in USD. See
// samples/DatabentoDotNet.Samples.HistoricalRange for the argument.
const decimal CostCeilingUsd = 0.01m;

const ulong MaxRecords = 10;

// A batch job is queued work, not a request that blocks. Minutes is the normal wait even for a job
// this small, so the poll is patient and gives up out loud rather than hanging.
const int PollMilliseconds = 5_000;
const int GiveUpAfterMinutes = 15;

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

var outputDirectory = Arg(4) ?? Path.Combine(Path.GetTempPath(), "databento-samples");

await using var client = new HistoricalClient { ApiKey = new ApiKey(key) };

var request = new SubmitJobParams
{
    Dataset = dataset,
    Symbols = Symbols.From(symbols),
    Schema = schema,
    DateTimeRange = DateRange.OnDay(parsedDate.Value).ToDateTimeRange(),
    Limit = MaxRecords,
    Encoding = Encoding.Dbn,

    // Zstd rather than None, and not only to save bytes: TimeseriesClient.OpenFileAsync decompresses
    // unconditionally, because what it exists to open is what GetRangeToFileAsync writes. Handing it
    // an uncompressed .dbn gets "Unknown frame descriptor" from the zstd decoder rather than
    // records. See ROADMAP.md §7.
    Compression = Compression.Zstd,

    // One file rather than one per day, so the download below has a single thing to open.
    SplitDuration = SplitDuration.None,
};

Console.WriteLine($"dataset          {request.Dataset}");
Console.WriteLine($"symbols          {request.Symbols.ToApiString()}");
Console.WriteLine($"schema           {request.Schema.ToWireString()}");
Console.WriteLine($"range            {InstantPattern.ExtendedIso.Format(request.DateTimeRange.Start)} .. {InstantPattern.ExtendedIso.Format(request.DateTimeRange.End)}");
Console.WriteLine($"output           {outputDirectory}");
Console.WriteLine();

var quoted = await client.Metadata.GetCostAsync(request.ToQuery());
Console.WriteLine($"quoted cost      ${quoted.ToString(CultureInfo.InvariantCulture)}");

if (quoted > CostCeilingUsd)
{
    Console.Error.WriteLine(
        $"That is more than the ${CostCeilingUsd.ToString(CultureInfo.InvariantCulture)} this sample "
        + "will spend, so no job was submitted. Narrow the range, or raise CostCeilingUsd if you "
        + "meant it.");
    return 3;
}

var job = await client.Batch.SubmitJobAsync(request);

// Printed before the poll loop rather than after it, and this is the line that matters if the
// sample is interrupted: the job is paid for from here on, and this id is how it is collected.
Console.WriteLine($"job id           {job.Id}");
Console.WriteLine();
Console.Write($"state            {job.State.ToWireString()}");

var deadline = SystemClock.Instance.GetCurrentInstant() + Duration.FromMinutes(GiveUpAfterMinutes);

while (job.State is not (JobState.Done or JobState.Expired or JobState.Purged))
{
    if (SystemClock.Instance.GetCurrentInstant() > deadline)
    {
        Console.WriteLine();
        Console.Error.WriteLine(
            $"Still {job.State.ToWireString()} after {GiveUpAfterMinutes} minutes. The job is paid "
            + $"for and keeps running — collect it later with job id {job.Id} rather than by "
            + "submitting another.");
        return 4;
    }

    await Task.Delay(PollMilliseconds);

    job = await client.Batch.GetJobDetailsAsync(job.Id);
    Console.Write($" -> {job.State.ToWireString()}");
}

Console.WriteLine();

if (job.State is not JobState.Done)
{
    Console.Error.WriteLine($"The job ended {job.State.ToWireString()}; there is nothing to download.");
    return 4;
}

// The job's own accounting, which is only filled in once it has finished. Asking for it at submit
// time gets nulls, because nothing has been processed yet.
Console.WriteLine($"records          {job.RecordCount?.ToString(CultureInfo.InvariantCulture) ?? "(unreported)"}");
Console.WriteLine($"billed           {(job.CostUsd is { } billed ? "$" + billed.ToString(CultureInfo.InvariantCulture) : "(unreported)")}");
Console.WriteLine();

foreach (var file in await client.Batch.ListFilesAsync(job.Id))
{
    Console.WriteLine($"  {file.Size,12} bytes  {file.Filename}");
}

Console.WriteLine();

// Files land in {OutputDirectory}/{JobId}/, and a partial file from an interrupted run is resumed
// rather than restarted.
var downloaded = await client.Batch.DownloadAsync(new DownloadParams
{
    JobId = job.Id,
    OutputDirectory = outputDirectory,
});

foreach (var path in downloaded)
{
    Console.WriteLine($"downloaded       {path}");
}

// A batch job delivers its metadata and a condition report alongside the data, so the .dbn.zst has
// to be picked out rather than assumed to be the only file.
var data = downloaded.FirstOrDefault(path => path.EndsWith(".dbn.zst", StringComparison.Ordinal));
if (data is null)
{
    Console.Error.WriteLine("No .dbn.zst among the downloaded files — nothing to decode.");
    return 5;
}

Console.WriteLine();

await using var reader = await TimeseriesClient.OpenFileAsync(data);

Console.WriteLine($"DBN v{reader.Metadata.Version}, stype_out {reader.Metadata.StypeOut.ToWireString()}");
Console.WriteLine();

var count = 0;
await foreach (var record in reader.ReadRecordsAsync())
{
    var when = DbnTime.TryToInstant(record.IndexTs, out var instant)
        ? InstantPattern.ExtendedIso.Format(instant)
        : "(no timestamp)";

    // RType is the wire's own name for the record shape, which is not always the schema's name for
    // it: a trade is rtype Mbp0 — market-by-price carrying zero book levels.
    var line = $"{when,-30}  {record.Header.RType,-16}  instrument {record.Header.InstrumentId,10}";

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
Console.WriteLine($"{count} record(s) read from {Path.GetFileName(data)}");

return 0;

string? Arg(int index) => args.Length > index && args[index].Length > 0 ? args[index] : null;
