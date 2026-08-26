# M1: DBN codec — implementation plan

Executes GitHub issues **#2–#6** (milestone *M1: DBN codec*). M1 is the bottleneck for both
M2 (live) and M3 (historical): each transport is DBN over a different pipe, so nothing
downstream can start until the codec decodes.

**Spec authority:** the `dbn` Rust crate **v0.68.0** for functionality, wire format, and
behaviour — *not* for structure. `databento-cpp` is the layout oracle. `PORTING.md` governs
how a Rust construct becomes a .NET one; where this plan and `PORTING.md` disagree,
`PORTING.md` wins and the conflict is a plan defect worth reporting.

## Reference clones (stable sibling paths)

| What | Version | Path |
|---|---|---|
| `dbn` — **the codec source; all of M1 ports from here** | 0.68.0 | `/Users/herbertsabanal/Projects/dbn` |
| `databento-rs` — client crate; source of test fixtures | 0.60.0 | `/Users/herbertsabanal/Projects/databento-rs` |
| `databento-cpp` — **size oracle** (`static_assert`s) | v0.66.0 | `/Users/herbertsabanal/Projects/databento-cpp` |

Pre-extracted upstream reference notes live in
`.superpowers/sdd/m1-dbn-codec/reference/` (git-ignored): `enums.md`, `publishers.md`,
`records.md`, `metadata.md`, `decoder.md`, `symbol-map.md`. **These are a convenience index,
not the authority.** Where an extract and the Rust source disagree, the Rust source wins —
say so in your report.

---

## Global Constraints

Every task is bound by these. A reviewer should treat a violation as a defect even when the
task text does not repeat it.

### G1 — Naming and layout

Namespace root is `DatabentoDotNet.Dbn`. **Never `Databento.*`** — that is the vendor's
namespace and an unreserved NuGet prefix. Target file layout:

```
src/DatabentoDotNet.Dbn/
  DbnConstants.cs          (exists)
  Enums/                   RType, Schema, SType, Compression, Encoding, Action, Side,
                           InstrumentClass, StatType, StatusAction, SystemCode, ErrorCode,
                           VersionUpgradePolicy, FlagSet, and the wire-string mapping
  Publishers/              Publisher, Dataset, Venue (mechanically derived from upstream)
  Records/                 RecordHeader (moves here) + the 17 record structs
  Metadata/                Metadata, MetadataDecoder, MetadataEncoder, SymbolMapping
  Decoding/                AlignedBuffer, DbnFsm, RecordRef, DbnDecoder
  SymbolMaps/              TsSymbolMap, PitSymbolMap
  Internal/                ZstdDecompressor.cs (exists)
```

One public type per file, file name == type name. File-scoped namespaces; `using` directives
outside the namespace (`IDE0065` is an error here).

### G2 — The build is strict, and it has already been probed

`TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`,
`GenerateDocumentationFile=true`, `Nullable=enable`, `EnforceCodeStyleInBuild=true`.

Consequences you must plan for:
- **Every public member needs an XML `<summary>`** or the build fails on CS1591. This includes
  every enum member and every public field of every record struct.
- `CA1711` fires on type names ending in `Enum`, `Flags`, `Delegate`, or `Attribute`. Upstream
  already avoids these (`FlagSet`, not `Flags`) — keep it that way.

**Already verified against this exact configuration** (probe built clean, zero warnings), so
do *not* add analyzer suppressions for them speculatively: `[Flags]` enums backed by `byte`;
enums backed by `ushort`; `[InlineArray]` structs; `public readonly` instance fields on
structs; `private readonly` reserved/padding fields; `MemoryMarshal.AsRef<T>` over a
`ReadOnlySpan<byte>`; `Unsafe.SizeOf<T>()`.

If you believe a suppression is genuinely required, put it in `.editorconfig` scoped to the
narrowest directory that needs it, with a comment giving the wire-format rationale — never a
bare `#pragma warning disable` at a use site.

### G3 — Multi-targeting: one seam, and you will not see the other side of it

`net10.0;net11.0`, where `net11.0` is enabled only when an SDK that can build it is installed.
**It is not installed on this machine**, so `#if NET11_0_OR_GREATER` branches are compiled
**only in CI**. The single permitted conditional-compilation seam in the whole library is
`Internal/ZstdDecompressor.cs`. Do not introduce a second one. If a task appears to need one,
stop and report it rather than adding it.

### G4 — Zero-copy is the reason this library exists

Records are reinterpreted in place over the read buffer via `MemoryMarshal.AsRef<T>`. Do not
add a copy for API convenience on the low-level path. Do not allocate per record. Do not use
LINQ, `string` conversion, or boxing inside decode.

**Do not use `System.IO.Pipelines`.** It is the reflexive .NET choice and it is wrong here:
`ReadOnlySequence<byte>` may be non-contiguous, which breaks `MemoryMarshal.AsRef<T>`, and it
layers a second buffer over the FSM's own.

### G5 — Numeric fidelity

- Prices stay `long` at fixed 1e-9 scale. `UndefPrice == long.MaxValue`. Never `decimal` on the
  decode path.
- Timestamps stay `ulong` nanoseconds. **`DateTime` ticks are 100 ns and would silently
  truncate.** Conversion helpers may exist, but must be explicit and off the hot path.
- Every multi-byte wire field is **little-endian**. On a big-endian host a raw reinterpret
  would be wrong; note it, but do not add byte-swapping — .NET has no supported big-endian
  target and speculative swapping costs throughput. A comment at the reinterpret site is enough.

### G6 — Error handling

`Result<T, E>` becomes exceptions for *exceptional* cases and `Try*` for *expected* ones.
A stream ending is not an exception. Malformed data is. Custom exceptions derive from a single
`DbnException` base.

### G7 — Rust idioms that must NOT survive the port

- **Type-state builders → `required` init properties.** Rust uses generic type-state for what
  C# 11 has natively.
- **Data-carrying enums → a status enum plus out-parameters**, or a `readonly struct` with
  factory methods. Do not emulate Rust enums with class hierarchies.
- **`State::Consume` must not be ported.** Its own doc comment admits it "gets around
  mutability requirements" — it models nothing about DBN and exists purely to defer a mutation
  the borrow checker forbids while a `data()` borrow is live. C# has no such restriction:
  consume immediately.

### G8 — Testing

xunit.v3. Test names are `Member_Scenario_Expectation` (CA1707 is already disabled under
`tests/`). Every record struct gets a size assertion **and** an alignment assertion. Where a
fixture exists, prefer it over a hand-rolled byte array — but keep at least one hand-built
byte-level test per wire structure so a bad fixture cannot mask a bad decoder.

Run `dotnet build && dotnet test` before reporting DONE. Report the actual counts.

### G9 — Scope discipline

Do not implement JSON or CSV encoding — upstream has them, this milestone does not
(`ROADMAP.md` defers them). Do not add async APIs in M1; the incremental decoder's whole point
is that the async I/O layer lives above it in M2. Do not add public API beyond what the task
names.

---

## Task dependency order

```
T1 enums ──┬── T2 publishers
           ├── T3 record structs (small) ──┬── T4 record structs (cstr/large)
           │                               │
           └── T5 fixtures + loader ───────┴── T6 metadata ── T7 AlignedBuffer
                                                                  │
                                              T9 symbol maps ── T8 DbnFsm + RecordRef + decoder
```

T1 unblocks everything. T2 is independent of T3–T9 once T1 lands. T5 is pure test
infrastructure and could run at any point, but is placed before T6 because T6 is the first
task whose definition of done needs a fixture.

---

## Task 1 — DBN enums and wire-string mapping

**Issue #2** (partial — the enum half). **Files:** `src/DatabentoDotNet.Dbn/Enums/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/EnumWireStringTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/enums.md`. Authority is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/enums.rs`, `enums/methods.rs`, `flags.rs`.

### Scope

Port these enums with their exact numeric values and backing types:

- `RType` — **23 variants**, with non-contiguous hex discriminants across `0x00..0xC4`. The
  Rust doc comment describes `0x00..0x0F` as an MBP-book-depth range, but only **3 of those 16
  values are valid discriminants** and `TryFrom` rejects the other 13. Port exactly the
  discriminants that exist; do not fabricate members to fill the range.
- `Schema`, `SType`, `Compression`, `Encoding`
- `Action`, `Side`, `InstrumentClass`, `StatType`, `StatusAction`, `SystemCode`, `ErrorCode`
  — note `StatType`, `StatusReason`, `ErrorCode`, and `SystemCode` are also non-contiguous
  (`StatType` jumps 26 → 10001; `ErrorCode` and `SystemCode` use `255` as an explicit `Unset`
  sentinel). Enumerate exactly; never assume a dense range.
- `FlagSet` — a `[Flags]` enum over `byte`
- `VersionUpgradePolicy` — `AsIs`, `UpgradeToV2`, `UpgradeToV3`; **default `UpgradeToV3`**
- `MatchAlgorithm`, `UserDefinedInstrument`, `SecurityUpdateAction`, `StatUpdateAction`,
  `StatusReason`, `TradingEvent`, `TriState`

The last group is **not** in issue #2's scope list, and that list is wrong: every one of those
seven is a char-valued field embedded in a record struct's wire layout — `InstrumentDefMsg`
carries `MatchAlgorithm`, `SecurityUpdateAction`, and `UserDefinedInstrument`; `StatusMsg`
carries `StatusReason`, `TradingEvent`, and `TriState`; `StatMsg` carries `StatUpdateAction`.
Leaving them out would either block Tasks 3–4 or push a wire-layout-critical enum into the
task least equipped to validate it. **The scope is the full contents of `enums.rs` plus
`flags.rs`** — 20 enums in `enums.rs` and `FlagSet` in `flags.rs`, 21 types total, per §0 of
`enums.md`.

Several are `#[non_exhaustive]` in Rust. C# has no equivalent; do not emulate one and do not
add a synthetic catch-all member. An unknown value already fails through the `Try*` path.

Port the `RType` ↔ `Schema` mappings (`enums.md` §1a, §1b) here — they are pure enum-to-enum.
**Do not** port the `RType` → record-struct dispatch (§1c): it needs structs that do not exist
until Tasks 3–4, and it lands with `RecordRef` in Task 8.

Backing type comes from the Rust `repr`, not from C# habit: a `u8` repr becomes `: byte`, a
`u16` repr becomes `: ushort`. Char-valued enums (whose variants are ASCII characters such as
`b'A'`) keep their character values — surface both the `char` and the enum.

Wire-string mapping in **both** directions, as a static class of `switch` expressions
(`DatabentoDotNet.Dbn.Enums.WireStrings` or per-enum extension methods — your call, but it
must be allocation-free and reflection-free, so no `[Description]` attributes and no
`Enum.Parse`).

### Definition of done

- Every **string-bearing** enum round-trips: value → wire string → value.
- **Seven enums have no wire strings at all** — `Side`, `Action`, `InstrumentClass`,
  `MatchAlgorithm`, `UserDefinedInstrument`, `SecurityUpdateAction`, and `TriState` have no
  `as_str`, no `FromStr`, and no `Display` upstream; their wire form is the raw ASCII byte.
  Do **not** manufacture strings for them to satisfy a round-trip. Their round-trip under test
  is value → byte → value.
- Wire strings are byte-identical to upstream. The test explicitly asserts `mbp-1`,
  `ohlcv-1s`, and `cbbo-1m` as named cases, because these are the three that a naive
  `ToString().ToLowerInvariant()` gets wrong.
- A test asserts that parsing an unknown wire string fails via the `Try*` path and does not
  throw.
- The two parse-failure modes stay distinct, as they are upstream: a numeric value that is not
  a valid discriminant (`num_enum::TryFromPrimitiveError`) and a string that is not a known
  wire string (`dbn::Error::Conversion`) are different failures, and the live client will want
  to tell them apart. Both still return `false` from `Try*` on the expected path per G6.
- Any parse-only alias documented in `enums.md` has a test showing it parses but is not
  emitted.
- `dotnet build && dotnet test` green; report counts.

### Porting notes

**Schema wire strings are not `ToString().ToLower()`.** They are `mbp-1`, `ohlcv-1s`,
`cbbo-1m`. Map every one explicitly. A naive lowercase conversion silently produces invalid
subscription strings that the live gateway rejects at runtime — the exact failure this task
exists to prevent.

---

## Task 2 — Publisher, dataset, and venue tables

**Issue #2** (the table half). **Files:** `src/DatabentoDotNet.Dbn/Publishers/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/PublisherTableTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/publishers.md`. Authority is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/publishers.rs`.

### Scope

`Publisher`, `Dataset`, and `Venue` enums plus the mapping functions between them
(publisher → venue, publisher → dataset, and each type's wire string in both directions).

**Derive these mechanically from the Rust source — do not hand-type the variant list.** The
upstream file is ~1900 lines; a hand transcription will contain errors that no test catches.

Write a generator script at `tools/generate-publishers.py` that parses `publishers.rs` and
emits the C#, and **commit both the generator and its output**. Upstream adds publishers with
almost every release, so regeneration is a recurring need, not a one-off — an uncommitted
generator makes the emitted file unreproducible and effectively hand-maintained from its first
update onward. The generator takes the path to `publishers.rs` as an argument (no hard-coded
absolute path) and is not part of the build.

The emitted file carries a header comment naming the upstream file, the crate version, and the
generator that produced it.

### Definition of done

- Variant counts for `Publisher`, `Dataset`, and `Venue` match the counts recorded in
  `publishers.md`, asserted by a test — so a truncated generation fails loudly.
- Round-trip test over **every** variant: value → wire string → value.
- `publisher → venue` and `publisher → dataset` agree with upstream for every publisher.
- The generated file carries a header comment: upstream file, crate version, and the note
  that it is generated.

---

## Task 3 — Record structs: header, book pairs, and the fixed-size messages

**Issue #3** (first half). **Files:** `src/DatabentoDotNet.Dbn/Records/*.cs` (and move the
existing `RecordHeader.cs` into `Records/`), `tests/DatabentoDotNet.Dbn.Tests/RecordLayoutTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/records.md`. Authority for layout is
`/Users/herbertsabanal/Projects/databento-cpp/include/databento/record.hpp` — its
`static_assert(sizeof(X) == N)` lines. Authority for field semantics is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/record.rs`.

### Scope — these structs and these sizes

| Struct | Wire size |
|---|---|
| `RecordHeader` | 16 |
| `MboMsg` | 56 |
| `BidAskPair` | 32 |
| `ConsolidatedBidAskPair` | 32 |
| `TradeMsg` | 48 |
| `Mbp1Msg` | 80 |
| `Mbp10Msg` | 368 |
| `BboMsg` | 80 |
| `Cmbp1Msg` | 80 |
| `CbboMsg` | 80 |
| `OhlcvMsg` | 56 |
| `StatusMsg` | 40 |

Each is `[StructLayout(LayoutKind.Sequential)] public readonly struct` with `public readonly`
fields in **declaration order matching the Rust exactly**, including every reserved/padding
field (declare padding as `private readonly` — it must occupy space but not appear in the API).

> **Trap:** the Rust structs carry `encode_order(N)` attributes. Those control **CSV and JSON
> column order only — never memory layout.** Transcribe **declaration order**. A field list
> built from `encode_order` compiles, passes a size assertion, and decodes garbage.

Surface `c_char` fields (`action`, `side`) both as `char` and as the corresponding enum from
Task 1. Fixed-size arrays of `BidAskPair` (`Mbp10Msg` carries ten) use `[InlineArray]`.

### The `IRecord<TSelf>` contract — define it here, everything downstream depends on it

Rust associates a record struct with the rtypes it decodes through the `HasRType` trait.
Nothing else in this plan carries that association, and Task 8's `RecordRef.Has<T>()` /
`TryGet<T>()` cannot dispatch without it. **Task 3 defines it:**

```csharp
public interface IRecord<TSelf> where TSelf : unmanaged, IRecord<TSelf>
{
    static abstract bool HasRType(RType rtype);
    static abstract int WireSize { get; }
}
```

C# 11 static abstract interface members are the direct analogue: the check is resolved at the
call site with no allocation, no boxing, and no reflection, so it stays AOT- and trim-safe.
Implement it on **every** record struct in this task; Task 4 continues it for the rest.
(`databento-cpp` models it the same way — a `static bool HasRType(RType)` per struct — which
is independent confirmation this is the right shape.)

> **`rtype` alone does not identify a record.** For five rtypes — `InstrumentDef`,
> `SymbolMapping`, `Error`, `System`, and `Statistics` — the *same rtype byte* decodes to a
> *different struct* depending on the record's wire length, because those five changed layout
> across DBN versions. So the match rule is `T.HasRType(rtype) && wireLength == T.WireSize`,
> with **exact** equality. A `>=` comparison would let a 520-byte v3 `InstrumentDefMsg` match
> `InstrumentDefMsgV1` (360) and silently decode as the wrong version.
>
> Exact size disambiguates every family cleanly — verified: `InstrumentDefMsg` 360/400/520,
> `StatMsg` 64/80, `ErrorMsg` 80/320, `SymbolMappingMsg` 80/176, `SystemMsg` 80/320. No two
> versions of the same rtype share a size.

This shape — including `RecordRef.TryGet<T>` constrained on
`where T : unmanaged, IRecord<T>` — has been probe-compiled against this repo's analyzer set
and produces zero warnings. Implementing an interface does not affect struct layout, so it
does not disturb the size assertions.

Also port the type aliases recorded in `records.md` (for example `TbboMsg` for `Mbp1Msg`) —
in C# these are `global using` aliases or thin wrapper types; prefer the alias, and say which
you chose and why.

### Definition of done

For **every** struct above, three assertions:
1. `Unsafe.SizeOf<T>()` equals the `databento-cpp` value in the table.
2. Alignment is 8 — assert via a `[StructLayout]`-based probe struct, not by inspection.
3. No interior padding beyond the fields explicitly declared — assert that the sum of the
   declared field sizes equals `Unsafe.SizeOf<T>()`. Reflection is fine here (this is a test,
   and G4's no-reflection rule governs the decode path, not the test project); walk
   `typeof(T).GetFields(Instance | Public | NonPublic)` and sum `Unsafe.SizeOf` of each field
   type. **`[InlineArray]` fields need care:** reflection reports a single element field, so
   size the buffer type itself rather than its element. Write this as one shared test helper,
   not once per struct.

Plus: one round-trip test that writes a known byte pattern and reads each field back at its
expected offset, for at least `MboMsg` and `Mbp10Msg` (the deepest nesting).

`dotnet build && dotnet test` green; report counts.

### Porting notes

Records are reinterpreted in place over the read buffer, so a layout error is **silent data
corruption**, not an exception. These size assertions are the only thing that turns it back
into a build failure. If your computed field-offset total disagrees with the `databento-cpp`
size, **stop and report it** — do not insert padding to make the number come out right.

---

## Task 4 — Record structs: C-string, variable, and large messages

**Issue #3** (second half). **Files:** `src/DatabentoDotNet.Dbn/Records/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/RecordLayoutTests.cs` (extend).

**Read first:** the same `records.md`, especially its **Version differences** section.

### Scope — these structs and these sizes

| Struct | Wire size |
|---|---|
| `InstrumentDefMsg` | 520 |
| `ImbalanceMsg` | 112 |
| `StatMsg` | 80 |
| `ErrorMsg` | 320 |
| `SymbolMappingMsg` | 176 |
| `SystemMsg` | 320 |

Plus the **six version-specific variants** that the v1/v2 → v3 upgrade path in Task 8 needs.
Task 8 consumes these; it must not have to invent them:

| Struct | Wire size |
|---|---|
| `InstrumentDefMsgV1` | 360 |
| `InstrumentDefMsgV2` | 400 |
| `StatMsgV1` | 64 |
| `ErrorMsgV1` | 80 |
| `SymbolMappingMsgV1` | 80 |
| `SystemMsgV1` | 80 |

Every other record is byte-identical across all three DBN versions — only these five families
changed (`InstrumentDefMsg` 360/400/520, `StatMsg` 64/64/80, `ErrorMsg` 80/320/320,
`SymbolMappingMsg` 80/176/176, `SystemMsg` 80/320/320).

And an `UpgradeTo` conversion per version-specific struct. **Read this before writing them:**

> An upgrade is a **value-level conversion into different-sized storage — never an in-place
> reinterpret.** You cannot `MemoryMarshal.AsRef` your way across a version boundary; the
> target is larger and its fields are in different places.
>
> Fields that did not exist in the older version take **their type's default, which for a price
> field is `UndefPrice` (`long.MaxValue`) — not zero.** Zero is a valid price; the sentinel is
> not. Getting this wrong produces plausible-looking zero prices in upgraded v1 data.
>
> `StatMsg`'s upgrade is the sharp one: `quantity` widens `int` → `long` in v3, and the
> conversion must **translate the sentinel itself** (`int.MaxValue` → `long.MaxValue`), not
> merely widen the number. A plain widening turns "undefined quantity" into the literal
> quantity 2,147,483,647.

Plus:
- `WithTsOut<T>` — the +8-byte wrapper carrying the gateway send timestamp. **Its constructor
  must recompute `hd.length` for the extra 8 bytes** — this is the one construction path that
  does not get length correctness for free, and a wrong `length` desynchronises the stream.
- A reusable fixed C-string helper: `[InlineArray]` over `byte`, decoded **lazily** to `string`
  (never eagerly on decode — that would allocate per record and defeat G4). Both the 71-byte
  v2+ length and the 22-byte v1 length must be expressible.

### Definition of done

Same three assertions per struct as Task 3 (size, alignment 8, no unexplained interior
padding) — **reusing the shared helper Task 3 created, not a second copy of it** — plus
`IRecord<TSelf>` implemented on each of these six structs. And:
- A C-string test proving a symbol shorter than the field decodes without trailing NULs, a
  symbol exactly filling the field decodes fully, and decoding allocates nothing until asked.
- `WithTsOut<T>` asserts `Unsafe.SizeOf<WithTsOut<TradeMsg>>() == 48 + 8`, and a separate test
  asserts its constructor set `hd.length` to the wrapped record's length plus 8.
- Size assertions for all six version-specific structs against the table above.
- An upgrade test per conversion. At minimum: a `StatMsgV1` whose `quantity` is `int.MaxValue`
  upgrades to a `StatMsg` whose `quantity` is `long.MaxValue` (**not** 2147483647), and an
  `InstrumentDefMsgV1` upgrade leaves every v3-only price field at `UndefPrice`, not zero.
- The M0 test `MaxRecordLength_CoversLargestRecordPlusTsOut` still passes and is now backed by
  the real `InstrumentDefMsg` (520) rather than a literal.

---

## Task 5 — Vendor the DBN fixtures and add a fixture loader

**Supports #4, #5, #6.** **Files:** `tests/DatabentoDotNet.Dbn.Tests/Data/**`,
`tests/DatabentoDotNet.Dbn.Tests/DatabentoDotNet.Dbn.Tests.csproj`,
`tests/DatabentoDotNet.Dbn.Tests/TestFixtures.cs`.

### Scope

Copy the fixture corpus from **`/Users/herbertsabanal/Projects/dbn/tests/data/`** into
`tests/DatabentoDotNet.Dbn.Tests/Data/`, **excluding the 10 `.dbz` files**. That is
**71 files, 36 KB** total. Upstream is Apache-2.0.

> **Take the corpus from the `dbn` crate, not from `databento-rs`.** `databento-rs/tests/data`
> holds only 24 files and **every one of them is DBN v1** — vendoring that set would leave the
> v2 and v3 decode paths and both upgrade paths (v1→v3, v2→v3) with zero fixture coverage.
> The `dbn` corpus covers v1 (15 files), v2 (18 tagged + 17 untagged-native), v3 (20), 50 zstd
> streams, and 7 metadata-less `.frag` fragments.

`.dbz` is excluded deliberately: legacy DBZ is a different container format, not merely an
older DBN version, and issue #4 puts it out of scope ("never emit version 0"). If DBZ support
is ever wanted it is its own issue.

Wire the fixtures into the test csproj so they land beside the test assembly
(`CopyToOutputDirectory="PreserveNewest"`, **globbed** — not 71 hand-written entries). Add a
`TestFixtures` static class exposing: the fixture directory, an enumeration of all fixtures,
filtered enumerations by DBN version and by compression, and a `Read(name)` helper.

Add a short `Data/README.md` recording provenance: upstream repo, crate version (0.68.0),
license, and the fact that these are verbatim copies with `.dbz` excluded.

### Definition of done

- A test asserts **exactly 71** fixture files are present and each is non-empty — a partial
  copy or a missing csproj glob then fails loudly rather than silently reducing coverage
  everywhere downstream.
- A test asserts the corpus contains at least one v1, one v2, and one v3 stream, and at least
  one `.zst` — so a future re-vendor cannot quietly drop a version's coverage.
- The fixture directory resolves from the test output directory on all three CI platforms
  (use `AppContext.BaseDirectory`, never a path relative to the source tree).

---

## Task 6 — DBN metadata header: decode and encode

**Issue #4.** **Files:** `src/DatabentoDotNet.Dbn/Metadata/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/MetadataTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/metadata.md`. Authority is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/metadata.rs`, `decode/dbn/sync.rs`,
`encode/dbn/sync.rs`.

### Scope

- **Prelude:** magic `"DBN"` (3 bytes) + `version: u8` (1) + `length: u32` little-endian (4).
  `DbnConstants.MetadataPreludeLength` is already 8.
  **`length` does NOT include the prelude** — it counts the fixed plus variable sections only.
  Confirmed at `lib.rs:142` ("Excludes magic string, version, and length") and by
  `MetadataEncoder::encoded_len` (`encode/dbn/sync.rs:145-149`), which computes the total as
  `calc_length().0 + 8`. Getting this wrong offsets the whole stream by 8 bytes.
- **Fixed section** (`MetadataFixedLength = 100`): dataset C-string, `schema: u16`
  (`NullSchema = ushort.MaxValue`), start/end/limit, `stype_in` / `stype_out` / `ts_out`,
  `symbol_cstr_len: u16` (**v2+ only — absent in v1**), reserved padding, and
  `schema_definition_length: u32`. Exact offsets and reserved-byte runs are in `metadata.md`.
- **Variable section:** `symbols`, `partial`, `not_found`, `mappings` — including the nested
  interval structure inside each mapping.
- **Encoder**, sufficient for byte-identical round-trip.
- Reject version > 3 with a clear `DbnException`. Never emit version 0 (legacy DBZ).

`Metadata` is a class (it owns heap data: string lists and mappings) with `required` init
properties per G7 — not a builder.

**The decode entry point takes a `ReadOnlySpan<byte>`, not a `Stream`.** Task 8's FSM reaches
the `Metadata` state holding a filled buffer, not a stream, and a `Stream`-only API would force
it to copy. A `Stream` convenience overload may sit on top; the span form is the primitive.

### Definition of done

- Metadata from **every** non-fragment fixture vendored in Task 5 decodes without error,
  across all three DBN versions. (`.frag` fixtures are metadata-less by definition — skip them
  here; Task 8 covers them.)
- Each one re-encodes **byte-identically** to the original metadata block. This is the real
  test; a decoder that quietly drops reserved bytes passes a field-by-field comparison and
  fails this one.
- **The byte-identical test must decode with `VersionUpgradePolicy.AsIs`.** The library
  default is `UpgradeToV3` (matching upstream), and under that default a v1 or v2 header
  re-encodes as v3 — so it cannot be byte-identical by construction. Assert the round-trip
  under `AsIs`, and separately assert that a v1 header decoded under `UpgradeToV3` reports
  version 3 and gains the `symbol_cstr_len` field.
- The v1 fixtures exercise the `symbol_cstr_len`-absent path directly — no hand-built block
  needed, but keep one hand-built v1 block as a byte-level test per G8.
- Version 4 is rejected with a typed exception, asserted by a test.

### Porting notes

`symbol_cstr_len` is absent in v1 and present from v2 — **the fixed section is not uniform
across versions**, so a single offset table is wrong. `NullRecordCount = ulong.MaxValue`.

---

## Task 7 — `AlignedBuffer`

**Issue #5** (first half). **Files:** `src/DatabentoDotNet.Dbn/Decoding/AlignedBuffer.cs`,
`tests/DatabentoDotNet.Dbn.Tests/AlignedBufferTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/decoder.md`, §AlignedBuffer.
Authority is `/Users/herbertsabanal/Projects/dbn/rust/dbn/src/decode/dbn/aligned_buffer.rs`.

### Scope

Backed by `ulong[]`, viewed as bytes through `MemoryMarshal.AsBytes<ulong>()`. `ulong[]` is the
direct analogue of Rust's `Box<[u64]>`: 8-byte aligned by construction, which is what makes
`MemoryMarshal.AsRef<T>` sound over it.

- `position` / `end` indices. `Consume` and `Fill` move indices only — **never memmove**.
- **Explicit** `Shift()` and `ShiftForSpace(needed)` so the copy is paid at refill boundaries
  and is visible in a profile, not hidden inside every read.
- Growth policy per `decoder.md`; default capacity 64 KiB; capacity may never fall below
  `DbnConstants.MaxRecordLength`.

### Definition of done

- A test asserts the byte view's address is 8-byte aligned (`Unsafe.AsPointer` / `nint % 8 == 0`)
  across several capacities, including one that is not a multiple of 8.
- Tests for: fill-consume-fill without shift, shift preserving unconsumed bytes, `ShiftForSpace`
  growing when the request exceeds capacity, and `ShiftForSpace` **not** copying when there is
  already room at the front.
- A test proves `Consume` performs no copy — assert via unchanged indices and an unchanged
  backing reference, not by timing.

---

## Task 8 — `DbnFsm`, `RecordRef`, and the incremental decoder

**Issue #5** (second half). **Files:** `src/DatabentoDotNet.Dbn/Decoding/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/DbnDecoderTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/decoder.md`. Authority is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/decode/dbn/fsm.rs`, `record_ref.rs`,
`compat.rs`, `decode/zstd.rs`.

### Scope

- States `Prelude` → `Metadata{length}` → `Record`. **`State::Consume` is not ported** (G7).
  Upstream has four states; `Consume { read, compat, compat_fill, expand_compat }`
  (`fsm.rs:54-74`) is the fourth and its own doc comment says it exists only to "get around
  mutability requirements". **Three states suffice.** Nothing replaces it: perform the buffer
  advance (`consume`/`fill`/reset on both buffers) immediately and inline at each point where
  the Rust constructs `State::Consume`, then return the decoded record from that same call.
- `ProcessStatus Process(out int bytesNeeded)` — a plain status enum plus an out-parameter, not
  a data-carrying enum (G7).
- Public surface: `Space()`, `Fill(n)`, `TryNextRecord(out RecordRef)`, `Reset()`,
  `HasDecodedMetadata`.
- `RecordRef` as a `ref struct` over the buffer, with `Has<T>()` and `TryGet<T>()`. The match
  rule is `T.HasRType(rtype) && wireLength == T.WireSize` — **exact** size equality, because
  five rtypes decode to different structs at different lengths (see Task 3). Cross-check
  against upstream's `rtype_dispatch_base!` macro at `macros.rs:14-69`, which is where that
  size-dependence actually lives, and handle `ts_out` explicitly: when metadata says `ts_out`
  is present the wire length carries +8 that must not be compared against a bare `WireSize`.
- v1/v2 → v3 upgrade via the compat buffer, honouring `VersionUpgradePolicy` from Task 1.
  **Use the version-specific structs and `UpgradeTo` conversions built in Task 4** — the
  upgrade is a value-level conversion into a larger buffer, never an in-place reinterpret, so
  the compat buffer must be sized for the v3 record, not the v1 one.
- Zstd framing through the existing `Internal/ZstdDecompressor` seam — **do not add a second
  `#if` seam** (G3). Detection is a **non-destructive 4-byte peek** compared little-endian as a
  `uint` against the zstd frame magic `0xFD2FB528` (`decode/zstd.rs:8-16`); upstream then wraps
  the reader as either `Zstd` or `Uncompressed` (`decode/dyn_reader.rs:75-87`) with **no bytes
  consumed on either branch**. A detection that consumes the bytes breaks the raw-DBN path.

### Definition of done

- Decodes **every** fixture from Task 5 — all 71, across v1/v2/v3, raw and zstd — asserting
  the record count per fixture against the count upstream expects.
- The 7 `.frag` fixtures decode through the metadata-less path (a fragment has no prelude and
  no metadata block; the FSM must be startable directly in the `Record` state).
- Both upgrade paths are covered by real data: v1→v3 and v2→v3. Assert that a v1 fixture
  decoded under `UpgradeToV3` yields v3-sized records, and that the same fixture under `AsIs`
  yields v1-sized ones.
- **The byte-at-a-time test:** feeding the decoder one byte per `Fill` produces output
  identical to a single bulk read, for every fixture. A TCP socket produces exactly this
  pattern, so this is the test that matters most in the task.
- A test feeding a truncated stream ends cleanly via the `Try*` path rather than throwing —
  a stream ending is not an exception (G6).
- A test feeding deliberately malformed data (bad magic, over-long record length) throws a
  typed `DbnException`.

### Porting notes

`TryNextRecord` is synchronous by design and `RecordRef` is a `ref struct`, so neither can
cross an `await`. That is not a limitation to work around — it is the boundary that keeps the
zero-copy path sound. The async I/O layer sits **above** this in M2 and calls `Fill` itself.

---

## Task 9 — Symbol maps

**Issue #6.** **Files:** `src/DatabentoDotNet.Dbn/SymbolMaps/*.cs`,
`tests/DatabentoDotNet.Dbn.Tests/SymbolMapTests.cs`.

**Read first:** `.superpowers/sdd/m1-dbn-codec/reference/symbol-map.md`. Authority is
`/Users/herbertsabanal/Projects/dbn/rust/dbn/src/symbol_map.rs`.

### Scope

- `TsSymbolMap` — time-series map; resolution varies by date.
- `PitSymbolMap` — point-in-time map, updated incrementally from `SymbolMappingMsg`.
- Construction from a decoded `Metadata.Mappings` (Task 6).
- Incremental update from live `SymbolMappingMsg` records (Task 4) — M2 depends on this path.

### Definition of done

- Resolving symbols from the `definition` fixture matches the mappings in that file's own
  metadata, for every instrument in the file.
- Date-boundary tests. **This is the subtlest thing in the file and the one place a careful
  port still goes wrong:** `PitSymbolMap::from_metadata` compares `date @ 00:00 UTC` against
  `metadata.end` at **full nanosecond precision, not date granularity** (`symbol_map.rs:241`,
  pinned upstream by `test_symbol_map_for_date_out_of_range`, `symbol_map.rs:870-890`). So an
  `end` of exactly midnight **excludes** that whole day, while an `end` one nanosecond later
  **includes** it. Truncating `end` to a date before comparing gets the boundary backwards.
  Port the comparison as-is and write both cases — midnight-exact and midnight-plus-1ns — as
  named tests.
- A miss returns `false` via `Try*` rather than throwing (G6).
