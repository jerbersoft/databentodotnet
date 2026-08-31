# FAQ

Short answers. Longer ones are linked.

---

## The project

### Is this an official Databento client?

**No.** Databento maintains official clients for Python, C++, and Rust. This is an independent
third-party port, not affiliated with or endorsed by them. Questions about datasets,
entitlements, billing, or the wire protocol itself belong with
[Databento support](https://databento.com/docs).

### Why `DatabentoDotNet.*` and not `Databento.*`?

`Databento.*` is the vendor's namespace and an **unreserved NuGet prefix they could claim at any
time**. Squatting it would be rude and fragile. "DotNet" also reads as .NET rather than .NET
Framework.

### Is it on NuGet?

Yes, at `0.9.1` — all four packages, tagged `v0.9.1`. `0.9.0` before it is the same code with a
package page that still linked to the retired wiki; `0.1.0-alpha` was a pipeline test rather than
something to build against.

```sh
dotnet add package DatabentoDotNet.Dbn
```

A beta, so pin the exact version: the API can still change before 1.0. See
[Getting Started](getting-started.md) to install, and [Release Notes](../release-notes.md) for what is in it.

### What works today?

All four clients. The DBN codec, live streaming, the historical client and reference data are
complete — milestones 0 through 4 — and published. What remains before 1.0 is the release itself:
the public API is locked, Native AOT is verified by running a native binary, and the live latency
benchmark is measured. See
[Release Notes](../release-notes.md).

### Can I use it in production?

It is pre-1.0 and the API will change. The codec is the most settled part — it decodes the whole
vendored conformance corpus and its struct layouts are pinned against `databento-cpp`'s
`static_assert`s by a test that runs on every build. Pin a commit, read the
[ROADMAP](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md), and expect
breaking changes before 1.0.

---

## Platforms and dependencies

### Which .NET versions?

**`net10.0` only.** No `netstandard2.0`, no .NET Framework, and neither is planned — the codec
uses `ref struct`s, `[InlineArray]`, and static abstract interface members, none of which exist on
the old runtime.

### Why not .NET 11?

A `net11.0` target existed briefly, to pick up `System.IO.Compression.ZstandardStream` from the
BCL and ship dependency-free. It was removed while .NET 11 is in preview: the preview SDK is not
installed on dev machines, so that branch was **compiled nowhere** — written, reviewed, and
shipped without ever passing a compiler — and CI inferred the target from the installed SDK,
meaning a failed preview-SDK resolution silently dropped it and still went green.

Every zstd call routes through one internal seam, so restoring the target at GA is a one-file
change.

### What does it depend on?

Two packages, both reaching consumers:

- **`ZstdSharp.Port`** — DBN's transport compression. Pure managed: no P/Invoke, no native asset,
  no per-RID build.
- **`NodaTime`** — all date and time handling, and it appears in the public API.

### Does it work with trimming and Native AOT?

Yes, and that is a hard requirement enforced by analyzers in the build. Both dependencies are
pure managed; nothing uses reflection on the decode path.

### Why NodaTime instead of `DateTime`?

**A `DateTime` tick is 100 nanoseconds and a DBN timestamp is nanoseconds.** The BCL cannot
represent one: `1609160400000000001` comes back as `…000`. `Instant` round-trips it exactly.

The repository bans all five BCL date/time types with an analyzer, tests included. You are not
bound by that in your own code. See [Timestamps and Prices](timestamps-and-prices.md).

---

## Using it

### `ApiKey` / `Symbols` is not in `DatabentoDotNet.Dbn` — where is it?

In the **root** `DatabentoDotNet` namespace. Those types are common to the live and historical
clients, so they sit above both. They still ship in the `DatabentoDotNet.Dbn` package; only the
`using` differs:

```csharp
using DatabentoDotNet;          // ApiKey, Symbols, UserAgent
using DatabentoDotNet.Dbn;      // the codec
using DatabentoDotNet.Live;     // LiveClient, Subscription
```

### Why are there two ways to read records?

`FillBufferAsync` + `TryNextRecord` allocates nothing per record; `RecordsAsync` is an
`await foreach` that costs ~110 bytes per record. The split is not a style choice — **an `async`
method cannot return a `ref struct`**, so `Task<RecordRef>` cannot exist. See
[Zero-Copy and Allocation](zero-copy-and-allocation.md).

### Why can't I put records in a list?

`RecordRef` is a `ref struct` pointing into a buffer the next read overwrites. Store
`OwnedRecord.CopyOf(record)`, or the concrete struct from `TryGet` — an ordinary struct has no
lifetime restrictions.

### Why `record.IndexTs` and not `record.Header.TsEvent`?

Most schemas index on `ts_recv`, not `ts_event`, and the two can fall on **opposite sides of UTC
midnight**. Keying a symbol lookup on the wrong one silently returns the previous day's symbol.
`IndexTs` picks the right field per record type.

### Why are prices `long`?

Fixed 1e-9 scale — `100_000_000_000` is `100.0`. `decimal` would cost throughput on the hot path,
and a record field's type *is* its wire layout. Convert at the boundary.

### When does billing start?

At **`StartAsync`**. `ConnectAsync`, `AuthenticateAsync`, and `SubscribeAsync` move no market data
— a subscription tells the gateway what to send later, and it sends nothing until the session
starts.

### Is `LiveClient` thread-safe?

**No, deliberately.** One connection is one conversation with the gateway and the record loop is a
single reader by construction. A lock would advertise a concurrency the protocol does not have.
Read on one thread and hand off decoded values.

### Does it reconnect automatically?

No. `ReconnectAsync` and `ResubscribeAsync` exist; when and how often to call them is a decision
about your deployment. A client that silently reconnected would silently resume billing.

### Can I subscribe after the session has started?

Yes. Same code path — the gateway distinguishes the two cases and the client does not need to.

### How many symbols can one subscription have?

Any number. The gateway caps a line at 500 and `SubscribeAsync` splits automatically, with
`is_last=1` on the final line only. `Symbols.All` subscribes to everything the dataset carries.

### Can it write DBN files?

**No, and there is deliberately not going to be an encoder.** This library reads market data;
nothing in it writes DBN, so an encoder would be a large public surface maintained for no
consumer. `MetadataEncoder` exists because the handshake needs it.

If writing `.dbn` files becomes a real requirement it gets an issue first.

### Does it support historical data?

Not yet — that is milestone 3, in progress. `DateRange` and `DateTimeRange` have landed;
`timeseries`, `symbology`, `metadata`, and `batch` have not.

---

## Contributing

### How do I report a bug or ask for a feature?

[Open an issue](https://github.com/jerbersoft/databentodotnet/issues/new/choose). The forms
require a **BLUF** — one or two sentences at the top saying what is wrong and what should happen
instead — because a reader should not have to scroll to learn what an issue is about.

### Can I send a pull request?

Yes, but **open the issue first**. Every change in this repository begins with one, including
chores and docs, and commits reference it (`Fixes #12`). An issue carries a milestone (M0–M5), one
`type:` label, and at least one `area:` label. The conventions are in
[`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md).

### Why is `CLAUDE.md` the contributor guide?

It is the repository's operating guide and happens to be written for an AI assistant, because
that is who does most of the work here. The conventions in it are the project's conventions
regardless of who is reading.

### How do I run the tests against a real gateway?

Copy `.env.example` to `.env` and set `DATABENTO_API_KEY`. That enables the `Category=Live` smoke
tests, which stop short of `start_session` and are free.

The one test that starts a session — and so moves billable data — needs a second gate,
`DATABENTO_LIVE_SESSION=1`. The rule is that no test starts a session without its own opt-in.

### Why is there no encoder / no `System.IO.Pipelines` / no `net11.0`?

Each was considered and rejected with reasons recorded. See
[`PORTING.md`](https://github.com/jerbersoft/databentodotnet/blob/master/PORTING.md) and
[`ROADMAP.md`](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — decisions
in this project are written down where the next person will find them.

---

## See also

- [Troubleshooting](troubleshooting.md) — specific errors and their fixes
- [Getting Started](getting-started.md) — a working program in ten minutes
