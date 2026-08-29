# Testing conventions

This page is for contributors. It describes how the repository tests against real Databento
services without spending money by accident, and why several of its tests are shaped the way they
are. If you are only *using* the packages, nothing here applies to you — though the argument in the
first section is the reason the library behaves as it does.

## The mock cannot confirm what it shares an author with

Each transport has a mock: `MockLiveGateway` for the live protocol, `MockHistoricalGateway` for the
HTTP API. Both are ported from upstream's own test doubles rather than invented, and both are
genuinely useful — they are how the read loops, the framing and the error paths get exercised on
every push, offline and for free.

They also cannot answer one class of question. The mock and the client were written from the *same
reading* of the protocol, so a misreading of the metadata block, the record framing, or a response
shape sits in **both** — and they agree with each other, forever, because agreeing is what they
were built from. A test suite that is green against a mock has established internal consistency,
not correctness.

**This is not hypothetical.** The first real call in M3 found a bug the mock had been agreeing with
for as long as both existed: `get_dataset_condition` reads its `end_date` as *inclusive*, while
`DateRange` models the range as half-open. Nothing offline could have caught it.

So each milestone ends with tests that call the real service. They are the second source, and they
are the only place a shared misreading can surface.

## Two gates, and the rule they encode

**No test spends without its own opt-in.** That is the rule; the two-gate structure is how it is
enforced rather than remembered.

**Gate one is the xunit category**, which keeps a whole class of test out of CI:

```sh
dotnet test --filter "Category!=Live&Category!=Historical&Category!=Reference"
```

That filter is what CI runs. Every test touching a real service carries one of those three
categories, so none of them execute on a runner — which has no credential anyway, making this the
second of two independent guards rather than the only one.

**Gate two is an environment variable, and only billable tests carry it:**

| Variable | Gates |
|---|---|
| `DATABENTO_LIVE_SESSION` | Starting a live session — the point where a live connection begins billing |
| `DATABENTO_HISTORICAL_REQUEST` | `timeseries.get_range` and `batch.submit_job` |
| `DATABENTO_REFERENCE_REQUEST` | The reference `get_range` endpoints and `security_master.get_last` |

Running the free real-API tests therefore needs a key and a category filter. Running the billable
ones needs a deliberate third act.

## The split is by file, so it stays checkable

Free and billable tests live in **separate classes in separate files**:

| Free | Billable, behind a second gate |
|---|---|
| `RealGatewaySmokeTests` — stops short of `start_session` | `RealGatewaySessionTests` — crosses it |
| `RealHistoricalApiTests`, `RealBatchApiTests` | `RealTimeseriesDownloadTests`, `RealBatchSubmitTests` |
| `RealReferenceApiTests` | `RealReferenceRequestTests` |

This is deliberate and worth preserving. "This class spends nothing" is a claim someone should be
able to verify by reading a file list, rather than by auditing every method in a large class for a
call that slipped in. A billable call added to a free file is a review finding, not a discovery
made from a bill.

Two other things follow the same principle:

- **`AllocateIsins` must be `false` in any reference test that reaches the real API without the
  gate.** It defaults to `true` and can create new ISIN allocations against an ISIN-limited plan,
  which spends something that is not money and is harder to get back.
- **The key never appears anywhere it could be read.** `.env` is git-ignored; the key is never
  printed, never placed in `argv` — `ps` can read that — never in a URL, and never in an exception
  message or an assertion message.

## What is asserted rather than measured

Three guarantees in this library are enforced on every `dotnet test`, because a benchmark somebody
has to remember to run cannot hold a guarantee:

**Struct layout.** The highest-value test in the repository asserts `Unsafe.SizeOf<T>()` for every
record struct against the `static_assert` values in `databento-cpp`. Records are reinterpreted in
place over the read buffer, so a layout mistake is silent data corruption rather than an exception
— these assertions turn it back into a build failure. **Add one for every record struct ported.**

**Zero allocation per record.** `AllocationTests` and `LiveAllocationTests` wrap
`GC.GetAllocatedBytesForCurrentThread()` around a steady-state loop and require **exactly zero** —
over the whole vendored corpus and over the mock gateway's socket. Both files also contain a test
proving the *measurement* notices a deliberate allocation, because a broken instrument reporting
zero would pass every other assertion in them.

**Decoder conformance.** Every `.dbn`, `.dbn.zst` and `.dbn.frag` fixture in the vendored corpus
decodes to the record count upstream reports for it.

## Native AOT is verified by running a binary

The trim and AOT analyzers have been on since the first milestone, and they are not the check.
`tools/DatabentoDotNet.AotProbe` publishes with `PublishAot` and **runs**, decoding the vendored
corpus to the same counts the managed suite asserts, from the same table both projects compile
rather than copy.

The publish is an independent gate rather than a slower rerun of the analyzers, and the reason is
concrete: ILC scans IL and has no idea a `#pragma warning disable IL2026` was ever written, so a
suppression that silences Roslyn does not silence it. `tools/aot-probe.sh` publishes, checks with
`file(1)` that what came out is a native executable rather than a managed assembly, and only then
runs it — the check has to be made from outside the process, because `PublishAot` writes
`IsDynamicCodeSupported=false` into the ordinary build's `runtimeconfig.json` too.

## Probe the endpoint you are about to change

A closing lesson, learned twice. When the inclusive-`end_date` bug above was fixed, the obvious
move was to fix it in the one shared renderer — `metadata.list_datasets` takes the identical
`DateRange`, and upstream documents nothing about *its* end. That endpoint was probed before the
change rather than after, and it turned out to be genuinely half-open. The shared fix would have
broken it silently.

**Probe the endpoint you are about to change, not the one next to it.**
