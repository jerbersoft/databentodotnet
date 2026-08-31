# Timestamps and Prices

**Read this before you compute anything from a record.** DBN's timestamps are `ulong`
nanoseconds, its prices are `long` at a fixed 1e-9 scale, and both use `MaxValue` as an
"undefined" sentinel. Every one of those three facts has an obvious-looking conversion that is
silently wrong.

---

## The rule in one line

**`ulong` nanoseconds on the wire and in the codec; [NodaTime](https://nodatime.org) at every
boundary above it.** `DbnTime` is the one crossing between them.

### Why record fields stay `ulong`

Records are reinterpreted in place over the read buffer, so a field's type *is* its wire layout.
The wire has an 8-byte `u64`. `Instant` is 16 bytes and `LocalDate` is 4. Putting a NodaTime type
in a record struct would not be a compile error — it would be silent data corruption, with every
field after it read from the wrong offset.

### Why not `DateTime`

A `DateTime` tick is 100 nanoseconds. A DBN timestamp is nanoseconds. The BCL literally cannot
represent one:

```
1609160400000000001 ns  →  Instant   →  1609160400000000001   ✓ exact
1609160400000000001 ns  →  DateTime  →  1609160400000000000   ✗ last digit gone
```

That is not a rounding nicety. Two events one nanosecond apart in an MBO stream — a cancel and the
replacement order — become simultaneous, and their order becomes whatever your sort happens to do
with a tie.

NodaTime reaches you transitively as a public dependency of `DatabentoDotNet.Dbn`, because
`Instant` and `LocalDate` appear in the public API. That was a deliberate cost, not an accident.

The repository enforces this on itself with `BannedApiAnalyzers`: `DateTime`, `DateTimeOffset`,
`DateOnly`, `TimeOnly`, and `TimeSpan` are build errors in every project, tests included. **You
are not bound by that in your own code** — convert an `Instant` to whatever your application uses.
Just be aware of what the conversion costs.

## Converting timestamps

```csharp
using DatabentoDotNet.Dbn;
using NodaTime;

ulong ns = trade.IndexTs;

DbnTime.IsUndefined(ns)                        // is this the sentinel?
DbnTime.TryToInstant(ns, out Instant instant)  // false when undefined
DbnTime.ToInstant(ns)                          // throws when undefined
DbnTime.TryToUtcDate(ns, out LocalDate date)   // false when undefined
DbnTime.ToUtcDate(ns)                          // throws when undefined
DbnTime.ToUnixNanoseconds(instant)             // back to the wire
DbnTime.ToUnixNanosecondsAtMidnightUtc(date)
```

Use the `Try*` pair when "no timestamp" is an ordinary outcome, and the throwing pair when a
missing timestamp means the data is wrong.

## The sentinel that survives a naive conversion

**DBN's undefined-timestamp sentinel is `ulong.MaxValue`, and the obvious cast wraps silently:**

```csharp
Duration.FromNanoseconds((long)DbnConstants.UndefTimestamp)   // -1 ns. No exception.
```

`Duration.FromNanoseconds` takes a `long`. `(long)ulong.MaxValue` is `-1`. That resolves to an
`Instant` one nanosecond *before* the Unix epoch — `1969-12-31T23:59:59.999999999Z` — which looks
like a real timestamp, sorts like a real timestamp, and is not one.

The sentinel is no safer as a date. It floor-divides to an entirely ordinary-looking day in 2554.

**Every `DbnTime` conversion checks the sentinel first.** Do not add a second conversion path that
skips the check, and do not hand-roll one:

```csharp
// Wrong. Silently produces 1969-12-31T23:59:59.999999999Z for an absent timestamp.
var t = Instant.FromUnixTimeTicks(0) + Duration.FromNanoseconds((long)ns);

// Right.
if (!DbnTime.TryToInstant(ns, out var t)) { /* no timestamp */ }
```

### The 2262 ceiling, and why `DbnTime` does not go through a single `long`

`long.MaxValue` nanoseconds since the epoch is the year **2262**. Any `ulong` above that overflows
a naive conversion, and there is a whole range of them below the sentinel.

`DbnTime` therefore splits the value into whole days plus a nanosecond-of-day remainder rather
than counting in a single `long`. Every `ulong` below the sentinel converts exactly:

```
ulong.MaxValue - 1  →  2554-07-21T23:34:33.709551614Z     ✓ not an overflow
ulong.MaxValue      →  undefined                          ✓ reported, not converted
```

## Which timestamp to use

**`record.IndexTs`, not `record.Header.TsEvent`.**

Fourteen of the twenty-one record structs carry a `ts_recv` and index on it; the rest have no
`ts_recv` at all and fall back to `ts_event`. `IndexTs` picks the right field per record type, and
`RecordRef.IndexTs` does it without knowing the concrete struct.

| Field | What it is |
|---|---|
| `Header.TsEvent` | When the venue says the event happened |
| `TsRecv` | When Databento's capture received it |
| `IndexTs` | Whichever of the two this record type is *indexed* by |
| `TsOut` | When the live gateway sent it. Present only if the session negotiated `ts_out` |

**The distinction is not cosmetic.** `ts_event` and `ts_recv` can fall on opposite sides of UTC
midnight. Resolve a symbol by the wrong one and you silently get the previous day's symbol, or
nothing at all, with no error anywhere. That is the exact failure `IndexTs` exists to prevent, and
it is why [Symbol Resolution](symbol-resolution.md) keys on `IndexTs` throughout.

For the date rather than the instant:

```csharp
using DatabentoDotNet.Dbn;

if (record.TryIndexDate(out LocalDate date)) { /* … */ }
LocalDate d = record.IndexDate();                   // throws when undefined
```

## Prices

**Prices are `long` at a fixed 1e-9 scale.** A price of `100_000_000_000` is `100.0`.

```csharp
const long Scale = DbnConstants.FixedPriceScale;    // 1_000_000_000

double display = trade.Price / (double)Scale;       // fine for printing
decimal exact   = trade.Price / (decimal)Scale;     // fine for arithmetic you will keep
```

`decimal` is not used on the wire or in the structs deliberately: it would cost throughput on the
hot path, and a record field's type is its wire layout. Convert at the boundary, as with
timestamps.

**Prefer integer arithmetic where you can.** Spreads, mid-points, and notional values are all
exact in the fixed-point representation and only stop being exact once you divide:

```csharp
long spread = ask - bid;                            // exact
long mid    = (ask + bid) / 2;                      // exact to 1e-9, which is the wire's own resolution
```

### The price sentinel

**`DbnConstants.UndefPrice` is `long.MaxValue`,** and it means "no price" — an unquoted side, a
book level that does not exist, a statistic that does not apply. Divided by 1e9 it becomes about
`9.22e9`, which is a number, not an error:

```csharp
if (level.BidPx == DbnConstants.UndefPrice) { /* no bid */ }
```

Check it before you compute a spread. An unquoted side otherwise produces a spread of roughly nine
billion dollars, and nothing anywhere reports a problem.

### Size sentinels

`DbnConstants.UndefOrderSize` is `uint.MaxValue`, with the same reasoning. `StatMsg` has its own:
`UndefStatQuantity` is `long.MaxValue` in v2+ and `int.MaxValue` in v1.

## The three sentinels, together

| Constant | Value | Naive conversion gives |
|---|---|---|
| `UndefTimestamp` | `ulong.MaxValue` | `1969-12-31T23:59:59.999999999Z`, or a day in 2554 |
| `UndefPrice` | `long.MaxValue` | `9223372036.854775807` |
| `UndefOrderSize` | `uint.MaxValue` | `4294967295` |

None of them throws. All three look like data. Check them.

## See also

- [Symbol Resolution](symbol-resolution.md) — why the symbol map keys on `IndexTs`
- [Decoding DBN Files](decoding-dbn-files.md) — record layouts and version differences
- [`CLAUDE.md`, "Dates and times"](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) — the repository's own rule, and how it is enforced
