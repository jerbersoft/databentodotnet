# Historical Data

**Two calls in this package cost money, and everything else is free — including the one that tells
you what they will cost.** This page covers the historical HTTPS API: pricing a request before you
send it, streaming a range, and running a batch job end to end.

For a first working program, start with [Getting Started](getting-started.md). For reading the records
once you have them, [Decoding DBN Files](decoding-dbn-files.md) applies unchanged — a historical
response is DBN over HTTPS rather than DBN on disk.

---

## What costs money

| Subclient | Endpoints | Billable |
|---|---|---|
| `client.Metadata` | `list_datasets`, `list_schemas`, `list_fields`, `list_publishers`, `list_unit_prices`, `get_dataset_range`, `get_dataset_condition`, `get_record_count`, `get_billable_size`, `get_cost` | No |
| `client.Symbology` | `resolve` | No |
| `client.Timeseries` | `get_range` | **Yes** |
| `client.Batch` | `list_jobs`, `get_job_details`, `list_files`, `download` | No |
| | `submit_job` | **Yes** |

That table is the most useful thing on this page. The historical API separates cost *by endpoint*,
not by session the way [Live Streaming](live-streaming.md) does, so you can explore a dataset, price a
request exactly, and resolve symbols without spending anything.

## Price the request, then send the same value

```csharp
using DatabentoDotNet;          // ApiKey, Symbols
using DatabentoDotNet.Dbn;      // Schema, the record structs
using DatabentoDotNet.Historical;
using NodaTime;

await using var client = new HistoricalClient { ApiKey = new ApiKey(apiKeyString) };

var request = new GetRangeParams
{
    Dataset       = "GLBX.MDP3",
    Symbols       = Symbols.From(["ESH4"]),
    Schema        = Schema.Trades,
    DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
    Limit         = 1_000,
};

decimal cost = await client.Metadata.GetCostAsync(request.ToQuery());
if (cost > 0.10m)
{
    Console.WriteLine($"${cost} for that range — narrowing it before pulling any data.");
    return;
}

await using var reader = await client.Timeseries.GetRangeAsync(request, ct);
```

`request.ToQuery()` is the point of this shape. `get_cost` prices **the exact request you are about
to send**, not a second one assembled by hand that might differ from it in a way that matters.
`SubmitJobParams` carries the same conversion.

The cost is a `decimal`, not a `double`. The API's own type is an `f64`, which is a Rust standard
library limitation rather than a decision — see [Timestamps and
Prices](timestamps-and-prices.md) for why this project does not do money in binary floating point.

## Reading the response

`GetRangeAsync` hands back a `TimeseriesReader`, which offers the same two loops `LiveClient` does
and for the same reason:

```csharp
// Objects. Simple, one allocation per record, right for most code.
await foreach (var record in reader.ReadRecordsAsync(ct))
{
    if (record.TryGet<TradeMsg>(out var trade)) { /* … */ }
}

// Or the zero-allocation pair: drain, then fill.
while (true)
{
    while (reader.TryNextRecord(out var record)) { /* … */ }
    if (await reader.FillBufferAsync(ct) == 0) break;
}
```

Which to use, and why the second one cannot be a single `await foreach`, is
[Zero-Copy and Allocation](zero-copy-and-allocation.md).

`reader.Metadata` carries the DBN version, `StypeOut`, the symbol mappings, and **`NotFound`** — the
symbols the server could not resolve. Check it. A misspelled symbol produces an empty result, not an
error.

To keep the bytes, `GetRangeToFileAsync` writes as it streams and returns a reader over the file.

> **`TimeseriesClient.OpenFileAsync` is zstd-only.** It decompresses unconditionally, because what
> it exists to reopen is what `GetRangeToFileAsync` wrote, and that is always zstd. Hand it a plain
> `.dbn` and the failure is `ZstdSharp.ZstdException: Unknown frame descriptor`, which does not
> explain itself. Use `DbnDecoder` for a file you did not download — it sniffs the stream.

## Date ranges are half-open

`DateRange` and `DateTimeRange` model `[start, end)`. The factory names say which end they mean, so
you do not have to remember:

```csharp
DateRange.OnDay(date);                 // that one day
DateRange.Between(start, end);         // [start, end)   — end excluded
DateRange.Including(start, lastDay);   // [start, lastDay] — lastDay included
DateRange.Spanning(start, duration);
```

`Including` exists because "up to and including the 5th" is what people usually mean, and one day
off is the easiest mistake in this API.

**One endpoint genuinely disagrees.** `get_dataset_condition` reads its `end_date` as *inclusive*
server-side. The client handles it; it is named here because it is the kind of thing that makes a
result look subtly wrong rather than fail, and because of how it was found — by calling the real
API, after the mock had been agreeing with the client about it for as long as both existed.

The near-miss is the better half of that story. `metadata.list_datasets` takes the identical
`DateRange` and upstream documents nothing about *its* end, so the obvious fix — convert once in the
shared renderer — was tested against the live API before being written. `list_datasets` turned out
to be genuinely half-open, and the shared fix would have broken it silently.

## Batch jobs

A batch job is queued work, not a request that blocks. Minutes is normal even for a small one.

```csharp
var job = await client.Batch.SubmitJobAsync(new SubmitJobParams
{
    Dataset       = "GLBX.MDP3",
    Symbols       = Symbols.From(["ESH4"]),
    Schema        = Schema.Trades,
    DateTimeRange = DateRange.OnDay(new LocalDate(2024, 1, 2)).ToDateTimeRange(),
    Encoding      = Encoding.Dbn,
    Compression   = Compression.Zstd,   // see the OpenFileAsync note above
    SplitDuration = SplitDuration.None, // one file, not one per day
});

Console.WriteLine(job.Id);              // print this BEFORE polling

while (job.State is not (JobState.Done or JobState.Expired or JobState.Purged))
{
    await Task.Delay(5_000, ct);
    job = await client.Batch.GetJobDetailsAsync(job.Id, ct);
}

var files = await client.Batch.DownloadAsync(new DownloadParams
{
    JobId           = job.Id,
    OutputDirectory = outputDirectory,
}, ct);
```

Three things that shape is arranged to get right:

- **Print the job id before the poll loop.** `SubmitJobAsync` is the billable act; polling,
  listing and downloading are free, and the files stay available until the job expires. If the
  process dies while polling, the data is paid for and that id is the only way back to it. Without
  it, the way back is to submit and pay again.
- **`CostUsd` and `RecordCount` are `null` right after submit.** Nothing has been processed yet.
  They fill in when the job reaches `Done`.
- **A job delivers `metadata.json`, `condition.json` and a manifest alongside the data**, so
  `DownloadAsync` returns several paths and the `.dbn.zst` has to be picked out rather than assumed
  to be the only one.

## Errors

Failures arrive as `DatabentoApiException`, carrying the structured detail rather than a status line:

```csharp
catch (DatabentoApiException ex)
{
    Console.Error.WriteLine($"{(int)ex.StatusCode} {ex.Case}: {ex.Message}");
    Console.Error.WriteLine(ex.DocsUrl);     // Databento's page for this error, when it sends one
    Console.Error.WriteLine(ex.RequestId);   // quote this in a support request
}
```

A stream ending is not an exception — `FillBufferAsync` returning `0` is how that is reported. See
[Troubleshooting](troubleshooting.md) for specific messages.

## See also

- [Getting Started](getting-started.md) — building, the API key, a first program
- [Decoding DBN Files](decoding-dbn-files.md) — reading the records this returns
- [Zero-Copy and Allocation](zero-copy-and-allocation.md) — which loop, and why there are two
- [Timestamps and Prices](timestamps-and-prices.md) — before computing anything from a record
- [Reference Data](reference-data.md) — security master and corporate actions, over this same transport
- [`ROADMAP.md` §5](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — the design decisions behind this client
- [The four samples](https://github.com/jerbersoft/databentodotnet/tree/master/samples) — `HistoricalRange` and `BatchDownload` are both flows above, runnable
