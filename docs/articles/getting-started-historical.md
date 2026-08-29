# Getting started: `DatabentoDotNet.Historical`

The historical HTTPS API — timeseries, batch jobs, symbology, and the metadata endpoints that price
a request before you send it.

```sh
dotnet add package DatabentoDotNet.Historical
```

Brings in `DatabentoDotNet.Dbn`, plus `Microsoft.Extensions.Logging.Abstractions` for the optional
`LoggerFactory`.

## The client and its four subclients

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Historical;

await using var client = new HistoricalClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};
```

| Subclient | Endpoints | Costs money? |
|---|---|---|
| `client.Metadata` | `list_datasets`, `list_schemas`, `list_fields`, `list_publishers`, `list_unit_prices`, `get_dataset_range`, `get_dataset_condition`, `get_record_count`, `get_billable_size`, `get_cost` | **No** — discovery and billing enquiries |
| `client.Symbology` | `resolve` | **No** |
| `client.Timeseries` | `get_range` | **Yes** |
| `client.Batch` | `submit_job`, `list_jobs`, `get_job_details`, `list_files`, `download` | **`submit_job` only** |

That column is the most useful thing on this page. The historical API separates cost *by endpoint*,
so you can explore a dataset, price a request exactly, and resolve symbols without spending
anything — and there are exactly two calls in the whole package that spend.

## Price the request, then send the same value

```csharp
using DatabentoDotNet.Dbn;
using NodaTime;

var request = new GetRangeParams
{
    Dataset = "GLBX.MDP3",
    Symbols = Symbols.From(["ESH4"]),
    Schema = Schema.Trades,
    DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
    Limit = 1_000,
};

decimal cost = await client.Metadata.GetCostAsync(request.ToQuery());
if (cost > 0.10m)
{
    Console.WriteLine($"${cost} for that range — narrowing it before pulling any data.");
    return;
}

await using var reader = await client.Timeseries.GetRangeAsync(request);
```

`request.ToQuery()` is the point of this shape. `get_cost` prices *the exact request you are about
to send*, not a second one assembled by hand that might differ from it in a way that matters. The
same conversion exists on `SubmitJobParams`.

The cost comes back as `decimal`, not `double`. The API's own type is an `f64`, which is a Rust
standard-library limitation rather than a decision; a per-gigabyte unit price gets multiplied by a
record count before anyone sees a figure, and that is not arithmetic to do in binary floating point.

## Reading the response

`GetRangeAsync` returns a <xref:DatabentoDotNet.Historical.TimeseriesReader>, which is the same
two-shaped reader the live client has:

```csharp
// Objects. Simple, allocates one per record, right for most code.
await foreach (var record in reader.ReadRecordsAsync())
{
    if (record.TryGet<TradeMsg>(out var trade)) { ... }
}

// Or the zero-allocation pair, drain-then-fill.
while (true)
{
    while (reader.TryNextRecord(out var record)) { ... }
    if (await reader.FillBufferAsync() == 0) break;
}
```

`reader.Metadata` carries the stream's DBN version, `StypeOut`, the symbol mappings, and
`NotFound` — the symbols the server could not resolve. Check it; a misspelling produces an empty
result rather than an error. See [the zero-copy contract](zero-copy.md) for which loop to reach for
and why.

To keep the data, `GetRangeToFileAsync` writes it as it streams and hands back a reader over the
file. `TimeseriesClient.OpenFileAsync` reopens one later — but note that it decompresses
**unconditionally**, because what it exists to open is what `GetRangeToFileAsync` wrote, and that is
always zstd. Hand it a plain `.dbn` and you get "unknown frame descriptor" from the zstd decoder
rather than records.

## Date ranges are half-open

<xref:DatabentoDotNet.Historical.DateRange> and <xref:DatabentoDotNet.Historical.DateTimeRange>
model `[start, end)`. The factory methods say which end they mean, so you do not have to remember:

```csharp
DateRange.OnDay(date);                    // that one day
DateRange.Between(start, end);            // [start, end)  — end excluded
DateRange.Including(start, lastDay);      // [start, lastDay]  — lastDay included
DateRange.Spanning(start, duration);
```

`Including` exists because "up to and including the 5th" is what people usually mean and
off-by-one-day is the easiest mistake in this API. **One endpoint genuinely disagrees:**
`get_dataset_condition` reads its `end_date` as *inclusive* server-side. That is handled inside the
client rather than left to you — it is mentioned here only because it is the kind of thing that
makes a result look subtly wrong, and it was found by calling the real API rather than the mock.

## Batch jobs

A batch job is queued work, not a request that blocks. Submit, poll, download, read:

```csharp
var job = await client.Batch.SubmitJobAsync(new SubmitJobParams
{
    Dataset = "GLBX.MDP3",
    Symbols = Symbols.From(["ESH4"]),
    Schema = Schema.Trades,
    DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
    Encoding = Encoding.Dbn,

    // Zstd rather than None: OpenFileAsync below decompresses unconditionally.
    Compression = Compression.Zstd,

    // One file rather than one per day, so there is a single thing to open.
    SplitDuration = SplitDuration.None,
});

Console.WriteLine(job.Id);   // print this before polling — see below

while (job.State is not (JobState.Done or JobState.Expired or JobState.Purged))
{
    await Task.Delay(5_000);
    job = await client.Batch.GetJobDetailsAsync(job.Id);
}

var files = await client.Batch.DownloadAsync(new DownloadParams
{
    JobId = job.Id,
    OutputDirectory = outputDirectory,
});
```

Three things this shape is arranged to get right:

- **Print the job id before the poll loop.** `SubmitJobAsync` is the billable act; everything after
  it is free and the files stay available until the job expires. If your process dies while
  polling, the data is already paid for and that id is how you collect it — without an id, the only
  way back to it is to submit and pay a second time.
- **`CostUsd` and `RecordCount` are null immediately after submit.** Nothing has been processed yet.
  They are populated once the job reaches `Done`.
- **A job delivers metadata and a condition report alongside the data**, so `DownloadAsync` returns
  several paths and the `.dbn.zst` has to be picked out rather than assumed to be the only one.

## Errors

Failures from the API arrive as <xref:DatabentoDotNet.Historical.DatabentoApiException>, which
carries the structured detail rather than just a status line:

```csharp
try
{
    await client.Timeseries.GetRangeAsync(request);
}
catch (DatabentoApiException ex)
{
    Console.Error.WriteLine($"{(int)ex.StatusCode} {ex.Case}: {ex.Message}");
    Console.Error.WriteLine(ex.DocsUrl);      // Databento's page for this error, when it sends one
    Console.Error.WriteLine(ex.RequestId);    // quote this in a support request
}
```

A stream ending is not an exception — `FillBufferAsync` returning `0` is how that is reported. The
exception type is for exceptional cases: authentication, entitlement, malformed requests, and
server errors.

## Logging

`HistoricalClient.LoggerFactory` is optional and takes an `ILoggerFactory`. Its main job is
surfacing the API's `X-Warning` header, which is why it exists at all: the alternative was every
one of twenty endpoints returning a wrapper type instead of its payload, just to carry a header
that is almost always absent.

## Where to go next

- [`samples/DatabentoDotNet.Samples.HistoricalRange`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.HistoricalRange)
  and
  [`samples/DatabentoDotNet.Samples.BatchDownload`](https://github.com/jerbersoft/databentodotnet/tree/master/samples/DatabentoDotNet.Samples.BatchDownload) —
  both flows above, runnable, each with a cost ceiling that makes `get_cost` load-bearing.
- [The zero-copy contract](zero-copy.md) · [Time](time.md)
- <xref:DatabentoDotNet.Historical.HistoricalClient>,
  <xref:DatabentoDotNet.Historical.TimeseriesReader> in the API reference.
