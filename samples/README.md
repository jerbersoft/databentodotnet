# Samples

Five console programs, each doing one thing, each written from a *consumer's* position rather than
from inside the library. That position is the point of having them: a sample that needs an awkward
two-step to do an obvious thing is the clearest evidence the public API has a problem, and these
landed after the API lock ([#63]) so that evidence arrives while the surface can still change.

| Sample | Shows |
|---|---|
| [`DatabentoDotNet.Samples.LiveStream`](DatabentoDotNet.Samples.LiveStream) | Connect, authenticate, subscribe, read, stop cleanly — over `FillBufferAsync`/`TryNextRecord` |
| [`DatabentoDotNet.Samples.HistoricalRange`](DatabentoDotNet.Samples.HistoricalRange) | Price a request with `metadata.get_cost`, then take it with `timeseries.get_range` |
| [`DatabentoDotNet.Samples.BatchDownload`](DatabentoDotNet.Samples.BatchDownload) | Submit a batch job, poll it, download the files, decode one |
| [`DatabentoDotNet.Samples.SymbolResolution`](DatabentoDotNet.Samples.SymbolResolution) | `symbology.resolve`, and the resulting map applied to decoded records |
| [`DatabentoDotNet.Samples.HostedLive`](DatabentoDotNet.Samples.HostedLive) | The same live session, run as a hosted `BackgroundService` and configured from `appsettings.json` instead of code |

## Running them

Every sample takes its credential from the environment and nothing else:

```sh
export DATABENTO_API_KEY=db-...
dotnet run --project samples/DatabentoDotNet.Samples.HistoricalRange
```

There is a `.env` file at the root of this repository and the test projects read it. That is harness
machinery. A sample that copied it would teach a reader to keep credentials in their source tree,
so none of these does — a missing key is a one-line error and exit code 1. `HostedLive` is no
exception: its `appsettings.json` carries the dataset, schema, symbols and reconnection policy, and
deliberately carries no key at all.

The first four take optional positional arguments, documented at the top of their `Program.cs`, and
each has defaults chosen so it runs with none:

```sh
dotnet run --project samples/DatabentoDotNet.Samples.LiveStream -- EQUS.MINI trades AAPL,MSFT 1400
dotnet run --project samples/DatabentoDotNet.Samples.HistoricalRange -- GLBX.MDP3 ESH4 trades 2024-01-02
```

`HostedLive` takes none — it is configured by editing
[`appsettings.json`](DatabentoDotNet.Samples.HostedLive/appsettings.json) instead, which is the
point of it:

```sh
dotnet run --project samples/DatabentoDotNet.Samples.HostedLive
```

## What they cost

**All five move billable data, and each says so in its own output before it does.** The defaults are
deliberately tiny — ten records, one settled day, an expired contract — and the three historical
samples ask `metadata.get_cost` first and refuse to spend more than a ceiling named in their source.
Widening the range in the arguments therefore fails loudly rather than quietly costing dollars.

The two live samples have no equivalent: there is no way to price a live session in advance, because
what it costs depends on what the market does while you are connected. Both bound themselves by
stopping after twenty records instead — `HostedLive` the same way `LiveStream` does, just from
inside its handler rather than its main loop.

Two things worth knowing before running either live sample:

- **A live subscription is silent when the market is closed.** `LiveStream` takes a replay-minutes
  argument to start in the past instead of at the live edge — `1400` replays yesterday's session.
  `HostedLive` takes no arguments at all, so its equivalent is a `Start` key added to the
  subscription in its [`appsettings.json`](DatabentoDotNet.Samples.HostedLive/appsettings.json) —
  `"Start": "2026-08-30T14:30:00Z"`, an ISO-8601 instant. There is no commented-out line to
  uncomment: JSON admits no comments, so the key has to be typed in.
- **The dataset must be one your account holds a *live* data license for**, which is a separate
  entitlement from historical access to the same dataset.

## How they are wired into the build

They are in `DatabentoDotNet.slnx`, so `dotnet build` builds them and so does CI. That is the whole
of the arrangement, and it is the arrangement: a sample outside the solution is a sample that rots
the first time the API moves under it. They cannot be *run* in CI — no runner has a key, and none
should — so building them is the only guarantee available, and it is the one that matters, since
every one of these compiles against the public surface in this working tree.

`samples/Directory.Build.props` keeps them out of `dotnet pack` and `dotnet test` by project
property rather than by a filter, and out of the public API lock, which has nothing to lock in a
file of top-level statements. None of that is about the samples themselves, which is why it lives
there rather than in the five project files: a reader who copies one of these out of the tree wants
the `ProjectReference` and nothing else.

[#63]: https://github.com/jerbersoft/databentodotnet/issues/63
