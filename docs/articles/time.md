# Time: NodaTime above the wire, `ulong` on it

**No method in this library accepts or returns a `DateTime`, `DateTimeOffset`, `DateOnly`,
`TimeOnly`, or `TimeSpan`.** Above the codec, dates and times are
[NodaTime](https://nodatime.org): `Instant`, `LocalDate`, `Duration`. On the wire, inside record
structs, they are `ulong` nanoseconds and nothing else.

That is two decisions, and they are worth separating because they are made for different reasons.

## Why not `DateTime`

A `DateTime` tick is 100 nanoseconds. A DBN timestamp is one nanosecond. The BCL type cannot
represent the value at all:

Take one timestamp straight off the wire — `1609160400000000001` nanoseconds since the Unix epoch —
and read it both ways:

| Read as | Comes back as |
|---|---|
| `DateTime` | `2020-12-28T13:00:00.0000000` — the trailing nanosecond is gone, and nothing reports it |
| NodaTime `Instant` | `2020-12-28T13:00:00.000000001Z` — round-trips exactly |

```csharp
Instant exact = DbnTime.ToInstant(1609160400000000001);
```

This is not a precision nicety. Databento's timestamps are nanosecond-resolution because the
ordering of events inside a microsecond is the thing a lot of this data is *for*. A type that
rounds them away turns two distinguishable events into one, silently, at the point where they stop
being distinguishable. That is a data-integrity failure dressed as a formatting detail, and no
amount of care at the call site prevents it once the type has been chosen.

So the ban is enforced by the compiler rather than by memory. `BannedSymbols.txt` at the repository
root lists all five types, `Microsoft.CodeAnalysis.BannedApiAnalyzers` reports each use as RS0030,
and `TreatWarningsAsErrors` makes that a build failure whose message names the NodaTime
replacement. It applies to the test projects too — a test that reached for `DateTime` to build an
expected value is precisely where a 100 ns truncation would get laundered into a passing assertion.

| Concept | Use | Never |
|---|---|---|
| A point on the timeline | `Instant` | `DateTime`, `DateTimeOffset` |
| A calendar date, no zone | `LocalDate` | `DateOnly` |
| A wall-clock date and time | `LocalDateTime` | `DateTime` |
| A time of day | `LocalTime` | `TimeOnly` |
| An elapsed amount | `Duration` | `TimeSpan` |
| A time in a specific zone | `ZonedDateTime` | `DateTimeOffset` |

**NodaTime is therefore a public dependency of every package**, not an implementation detail. It
appears in the surface — `Instant` on <xref:DatabentoDotNet.Dbn.DbnTime>, `LocalDate` on the symbol
maps, `Duration` on every `LiveClient` timeout — so consumers take it too. That was a deliberate
cost, weighed and accepted, rather than a dependency that crept in.

## Why record fields stay `ulong`

Records are reinterpreted *in place* over the read buffer, so a record struct field's type **is**
its wire layout. `Instant` is 16 bytes and `LocalDate` is 4; the wire has an 8-byte `u64` there.
Putting a NodaTime type in a record struct would not fail to compile — it would move every field
after it and read the wrong bytes. Silent corruption, not an error.

So the split is: **`ulong` in the structs and the codec, NodaTime at every boundary above them.**
Conversions, symbol maps, metadata, and anything a consumer calls are NodaTime. The structs are
not, and that is not an inconsistency to be tidied up later.

## `DbnTime` is the one crossing

<xref:DatabentoDotNet.Dbn.DbnTime> converts between the two, in both directions:

```csharp
Instant   when = DbnTime.ToInstant(record.IndexTs);
LocalDate day  = DbnTime.ToUtcDate(record.IndexTs);

ulong     back = DbnTime.ToUnixNanoseconds(when);
ulong midnight = DbnTime.ToUnixNanosecondsAtMidnightUtc(day);
```

There is deliberately no implicit conversion and no second path. Every conversion goes through this
type, because every conversion has to check the sentinel first.

## The sentinel, and why the obvious conversion is wrong

DBN's undefined-timestamp sentinel is `DbnConstants.UndefTimestamp` — `ulong.MaxValue`. The
straightforward-looking conversion is silently, confidently wrong:

```csharp
// Duration.FromNanoseconds takes a long. The cast wraps. No exception is thrown.
Duration.FromNanoseconds((long)DbnConstants.UndefTimestamp)   // -1 ns
```

That resolves to an `Instant` one nanosecond *before* the Unix epoch — 1969, in the middle of a
2024 dataset, arriving as an ordinary-looking value rather than as an error. Reading it as a date
is no safer: it floor-divides to a perfectly plausible day in 2554.

`DbnTime` checks the sentinel on every conversion, and offers both shapes:

```csharp
// Expected absence — a schema where the field is legitimately unset.
if (DbnTime.TryToInstant(record.IndexTs, out var instant))
{
    Console.WriteLine(InstantPattern.ExtendedIso.Format(instant));
}
else
{
    Console.WriteLine("(no timestamp)");
}

// Unexpected absence — you believe this field is always set, and want to know if it isn't.
Instant definitely = DbnTime.ToInstant(record.IndexTs);   // throws on the sentinel

// Or ask directly.
bool missing = DbnTime.IsUndefined(record.IndexTs);
```

`Try*` reports "no timestamp" by returning `false`; the non-`Try` pair throws. Which one is right
depends on whether an absent timestamp is expected in your schema, which is why both exist. **Do
not add a third conversion path that skips the check.**

## The 2262 ceiling, and why `DbnTime` avoids it

The sentinel is not the only hazard in the `ulong` → NodaTime crossing. `long.MaxValue`
nanoseconds is the year **2262** — so any conversion routed through a single `long` nanosecond
count overflows for a large part of the `ulong` range, sentinel or no sentinel.

`DbnTime` therefore splits the value into whole days plus a nanosecond-of-day remainder rather than
converting through one `long`. Every `ulong` below the sentinel converts exactly:
`ulong.MaxValue - 1` comes out as `2554-07-21T23:34:33.709551614Z`, not an overflow and not a
wrapped negative. No real dataset carries a 2554 timestamp, of course — the point is that the
conversion has no cliff inside its own domain, so a corrupt or unusual value produces an honest
answer instead of a wrong one.

## `IndexTs`, not `Header.TsEvent`

A related trap, since it also produces a wrong answer rather than an error. Most schemas — trades
included — index on `ts_recv` rather than `ts_event`, and the two can fall on opposite sides of UTC
midnight. Keying a symbol lookup on `ts_event` therefore returns the *previous day's* symbol, with
nothing looking broken.

`RecordRef.IndexTs` and `OwnedRecord.IndexTs` pick the correct field per record type. Use them for
anything date-keyed, and reach for `Header.TsEvent` only when you specifically mean the event time.

```csharp
// Right: the timestamp this record is indexed by.
symbolMap.TryGetSymbol(record, out var symbol);          // uses IndexTs internally
DbnTime.ToUtcDate(record.IndexTs);

// Wrong for a date-keyed lookup, even though it compiles and usually agrees.
DbnTime.ToUtcDate(record.Header.TsEvent);
```

## Formatting

`Instant.ToString()` is fine for a log line, but `NodaTime.Text` patterns are what you want for
anything a person reads in a column:

```csharp
using NodaTime.Text;

InstantPattern.ExtendedIso.Format(instant);   // 2024-01-02T00:00:06.05810217Z
LocalDatePattern.Iso.Format(date);            // 2024-01-02
```

Two things that bite in practice. `InstantPattern.ExtendedIso` trims trailing fractional zeros, so
the strings vary in width and a fixed-width column needs explicit padding. And `LocalDate`'s own
`ToString()` yields the culture's long date pattern rather than an ISO date, so `LocalDatePattern`
is the one to reach for when you want `2024-01-02`.

## See also

- <xref:DatabentoDotNet.Dbn.DbnTime> and <xref:DatabentoDotNet.Dbn.DbnConstants> in the API reference.
- [The zero-copy contract](zero-copy.md) — the other reason record struct fields cannot change type.
