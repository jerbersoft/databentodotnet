---
_layout: landing
---

# DatabentoDotNet

A .NET client for [Databento](https://databento.com) market data — real-time streaming, historical
data, and reference data, over a zero-copy DBN codec.

Databento maintains official clients for Python, C++, and Rust. There is no official .NET one, so
this fills the gap: the wire format is ported from the normative
[`databento/dbn`](https://github.com/databento/dbn) Rust implementation, and every record struct's
layout is pinned against the `static_assert`s in
[`databento-cpp`](https://github.com/databento/databento-cpp).

> [!NOTE]
> This is a third-party client. It is not published or endorsed by Databento.

## The four packages

| Package | What it does | Start here |
|---|---|---|
| `DatabentoDotNet.Dbn` | The DBN codec — record structs, metadata, decoder, symbol maps | [Getting started](articles/getting-started-dbn.md) |
| `DatabentoDotNet.Live` | Real-time and intraday-replay streaming over the raw TCP gateway | [Getting started](articles/getting-started-live.md) |
| `DatabentoDotNet.Historical` | Historical HTTPS API — timeseries, batch, symbology, metadata | [Getting started](articles/getting-started-historical.md) |
| `DatabentoDotNet.Reference` | Security master, corporate actions, adjustment factors | [Getting started](articles/getting-started-reference.md) |

`DatabentoDotNet.Dbn` is the only one with no sibling dependency; each of the other three brings it
in. There is nothing to choose between them at install time — take the one for the transport you
need.

## Two things worth reading before you write any code

The API reference below documents every public member, and there are two rules it cannot state in
any one member's remarks because they are properties of the whole library:

- **[The zero-copy contract](articles/zero-copy.md)** — a `RecordRef` points *into* the read buffer.
  It is valid until the next call on the decoder and no longer. This is the single thing most
  likely to be got wrong, and getting it wrong reads stale bytes rather than throwing.
- **[Time: NodaTime above the wire, `ulong` on it](articles/time.md)** — no method in this library
  accepts or returns a `DateTime`, and that is deliberate rather than an omission. A `DateTime`
  tick is 100 nanoseconds and a DBN timestamp is one nanosecond, so the BCL type cannot represent
  the value at all.

## A first decode

```csharp
using DatabentoDotNet.Dbn;

using var decoder = new DbnDecoder(File.OpenRead("data.dbn.zst"));   // zstd is detected, not declared

while (decoder.TryNextRecord(out RecordRef record))
{
    if (record.TryGet(out TradeMsg trade))
    {
        Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)}  {trade.Price}  x{trade.Size}");
    }
}
```

## Runnable samples

Four console programs live in the repository under
[`samples/`](https://github.com/jerbersoft/databentodotnet/tree/master/samples) — a live stream, a
historical range, a batch download, and symbol resolution applied to decoded records. Each takes its
key from `DATABENTO_API_KEY`, runs with no arguments, and says what it costs before it spends
anything. The getting-started pages link to the relevant one rather than repeating it.
