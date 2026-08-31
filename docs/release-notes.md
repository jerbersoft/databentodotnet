# Release Notes

**`0.9.1` is published — the beta.** It is `0.9.0`'s code exactly, republished because two
things reach you only through a package: the READMEs on the package pages, and the XML documentation
your editor reads. `0.1.0-alpha` before both was a pipeline test. This page holds the versioning
policy, where releases live, and the narrative for each one.

Last updated against `master`, 2026-08-31.

---

## Where releases will live

These are the canonical sources, in this order:

1. **[GitHub Releases](https://github.com/jerbersoft/databentodotnet/releases)** — the release of
   record. Generated from a tag, listing the issues closed since the previous one.
2. **NuGet** — `DatabentoDotNet.Dbn`, `.Live`, `.Historical`, `.Reference`, versioned together.
3. **This page** — the narrative: what changed for *you*, what to do about it, and the upgrade
   notes that do not fit a changelog line.

**This page is not the changelog.** A mechanical list of changes belongs in the repository, in the
commit that makes the change, where a reviewer sees it move. What belongs here is the part a
changelog is bad at: why a breaking change was made, and what to do about it. See the
[documentation policy in `CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) for the general rule.

> **Recommended, not yet done:** the repository has no `CHANGELOG.md`. Adding one in
> [Keep a Changelog](https://keepachangelog.com) format, maintained in the pull request that makes
> each change, would give the mechanical list a home that reviewers see. That needs an issue
> first, per the repository's own workflow.

## Versioning policy

**Semantic versioning, with the usual pre-1.0 caveat: the minor version is where breaking changes
go until 1.0.**

- **`0.x` — now.** The public API will change. Pin an exact version. Breaking changes are labelled
  `breaking-change` on their issue and tracked closely.
- **`0.9.0` — the beta** ([#74]), **`0.9.1`** ([#85]) the same code with corrected package metadata.
  Parity with `databento-rs` is met, so the *code* condition for 1.0 is satisfied; what is not yet
  known is whether the public surface is the right shape, because nothing has built against this
  library in anger. The beta is what buys that evidence.
- **`1.0` is reserved for full parity with `databento-rs`** — live, historical, and reference data
  — not for "it works". Parity turned out to be the cheaper half. The expensive half is the
  *promise*: 1.0 undertakes not to break a 3,801-member surface, which is why it waits on the beta
  rather than on the code.
- After 1.0, ordinary semver: breaking changes wait for a major.

**Why `0.9.0` and not `0.9.0-beta`.** A `-beta` suffix makes it a prerelease, and NuGet hides
prereleases from an ordinary `dotnet add package` unless the caller opts in. That friction lands on
exactly the people whose feedback the release exists to collect. `0.x` already tells both SemVer
tooling and human readers to expect change, so the suffix would buy a signal that is already there
and charge for it in adoption.

**The public API baseline moves at 1.0, not before.** `PublicAPI.Shipped.txt` should list a surface
we have undertaken not to break; `PublicAPI.Unshipped.txt` holds everything else. The dividing line
is not *published* — every `0.x` here reaches nuget.org — but *promised*. Freezing the surface in a
file named *Shipped* during the beta would assert the opposite of what the beta is for.

The version is set in
[`Directory.Build.props`](https://github.com/jerbersoft/databentodotnet/blob/master/Directory.Build.props)
and all packages ship together at the same version. A consumer taking `.Live` and `.Dbn` at
different versions is not a configuration anyone tests.

---

## 0.9.1 — 31 August 2026

**A documentation patch, and nothing else** ([#85]). The same code as `0.9.0` — you can upgrade
without reading further, and if you are not upgrading you are missing nothing that runs.

```sh
dotnet add package DatabentoDotNet.Live --version 0.9.1
```

Published 31 August 2026, all four packages, tagged `v0.9.1` and released by the `release: published`
trigger (run 33416260992). Verified against the artefacts pulled back off the feed rather than a
local pack — the standard [#71] set — including that every push in the log returned `Created` rather
than a skipped duplicate, which a green tick alone does not establish.

### Nothing changed, and that is checkable

`git diff v0.9.0..v0.9.1 -- 'src/**/*.cs'` contains **no non-comment line**, and all four
`PublicAPI.Unshipped.txt` files are byte-identical to `v0.9.0`. The 3,801-member surface is exactly
where it was. `0.9.1` promises nothing `0.9.0` did not.

### So why publish at all

Because two things reach you *only* by being inside a package, and a published package cannot be
edited.

**The package pages linked to a wiki that no longer exists.** [#82] moved the guides onto this site
and retired the wiki. Every source file was corrected in the same commit — but `0.9.0` had already
been packed, and nuspec metadata is frozen at pack time. So all four `0.9.0` pages on nuget.org
carried four wiki URLs each, in the README body and in the release-notes link, with no way to
correct them in place. The `0.9.1` pages link here instead.

Those `0.9.0` links are degraded rather than dead — with the wiki disabled GitHub redirects every
`/wiki/*` path to the repository home page — which is why this is a patch release and not a hotfix.

**The XML documentation gained worked examples.** [#78] added `<example>` blocks across all four
packages. Those ship inside the `.xml` in the `.nupkg`, which is what your editor reads, so on
`0.9.0` they reach the [API reference](api/index.md) on this site and *not* IntelliSense at the call
site. Upgrading is what closes that gap.

### "Project website" now opens this site

`PackageProjectUrl` was the GitHub repository. From `0.9.1` it is
`https://jerbersoft.github.io/databentodotnet/`, which is what NuGet renders as **Project website**.
"Source repository" beside it is unchanged and still goes to GitHub, so nothing is lost from the
page.

This reverses a decision recorded on [#68], which chose the repository URL partly because it
redirects through a rename and partly because — quoting it — *"the wiki is the landing page this
would otherwise have needed"*. The second reason stopped being true when [#82] built this site. The
first is still real, and a `github.io` URL is genuinely weaker than a repository URL, since renaming
the repository would break it without a redirect. It is reversed anyway, because each package README
already hardcodes several links to this site and those freeze into the package page the same way —
the risk was taken already, so pointing "Project website" here costs nothing new.

### Upgrading

Change the version. There is nothing else to do.

```xml
<PackageReference Include="DatabentoDotNet.Live" Version="[0.9.1]" />
```

Still pinned exactly, and still for the reason `0.9.0` gave: this is a beta, the public API can
change before 1.0, and [#68] is where that lands.

---

## 0.9.0 — 30 August 2026

**The beta.** All four packages, tagged `v0.9.0`, published by the `release: published` trigger
(run 33303094547). This is the first version meant to be built against.

```sh
dotnet add package DatabentoDotNet.Live
```

### What is in it

Everything. The library is code complete against `databento-rs`: all nineteen historical endpoints,
the live client, all four reference endpoints, and the DBN codec underneath them. 1,868 tests, zero
warnings, a public API locked by an analyzer, and Native AOT verified by publishing and *running* a
native binary. The two constructs that do not port one-to-one are recorded decisions rather than
gaps — `next_record` cannot be `async` around a `ref struct`, so it is the
`FillBufferAsync`/`TryNextRecord` pair, and there is deliberately no record encoder.

### Why it is not 1.0

Parity was the condition 1.0 was reserved for, and parity is met. It turned out to be the cheaper
half. The expensive half is the *promise*: 1.0 undertakes not to break a 3,801-member public
surface, and until this release nothing had built against the library in anger, so there was no
evidence the surface was the right shape. **0.9.0 is what buys that evidence.** If something in the
API is awkward to call, an issue now costs far less than a major version later.

`0.9.0` and not `0.9.0-beta`, deliberately — see [Versioning policy](#versioning-policy) above.

### Packaging, fixed

`0.1.0-alpha` shipped with no `projectUrl`, no `readme`, no `icon` and no `releaseNotes` — [#71]
read all four of its nuspecs and found them absent, and NuGet had been logging `warn : Readme
missing` on every push. All four are present now, and each package carries a `LICENSE` file as well
([#72]), since Apache-2.0 §4(a) asks that recipients of a derivative work receive a copy and the
`.nupkg` is the distribution.

Each package has **its own** README rather than a copy of the repository's, whose relative links
resolve on github.com and 404 on nuget.org. Their code samples were compiled against the real
assemblies rather than proofread, which caught three wrong ones before they became permanent on a
package page.

### Verified, not assumed

Against the *published* artefacts, downloaded back off the feed:

- All four `.nupkg` carry the metadata above and the `LICENSE`/`README.md`/`icon.png` files.
- All four install from a clean feed into a fresh project with `NUGET_PACKAGES` pointed at an empty
  directory, compile, and **run**.
- The resolved closure is exactly eight packages — the four of ours plus NodaTime,
  `ZstdSharp.Port`, `Microsoft.Extensions.Logging.Abstractions`, and the
  `DependencyInjection.Abstractions` that the last of those declares. No analyzer or build-only
  package leaked.
- All four PDBs come back from `symbols.nuget.org`.

### The release pipeline learned two things

Both defects in `publish.yml` were fixed before this release, and both earned themselves on it.

The version is now read off the packed artefact — the log opens `Packed version: 0.9.0` where a
hardcoded `0.1.0-alpha` used to sit, which at any other version would have operated on a version the
run did not produce and still gone green.

A run that would publish nothing now **fails**, via a pre-flight against the feed. `--skip-duplicate`
reports success when a package already exists, and a skipped primary push skips its `.snupkg` too,
so run 33280134279 was green having published nothing at all. `--skip-duplicate` stays, because it
is what lets a *partial* failure be retried; the pre-flight is what tells the two apart.

A third defect surfaced while fixing those. The final step, named "List packages on NuGet.org",
POSTed to `/api/v2/package/{id}/{version}` with the API key — an endpoint that **relists** a version
rather than listing anything. With its hardcoded version it would have quietly relisted an old
prerelease on every future release. It is now a read-only check that the versions this run published
actually reached the feed, and **that check took five minutes to go green** (09:04:23 → 09:09:25,
`.Live` last). A single post-push assertion would have failed this release spuriously. Do not shorten
the retry loop.

---

## 0.1.0-alpha — 29 August 2026

*[Release](https://github.com/jerbersoft/databentodotnet/releases/tag/v0.1.0-alpha.1) ·
tag `v0.1.0-alpha.1` · commit `700145c`*

The first published version: `DatabentoDotNet.Dbn`, `.Live`, `.Historical` and `.Reference`, all
four at `0.1.0-alpha`. It contains milestones 0 through 4 — the codec, live streaming, the
historical client and reference data.

**Alpha means the surface can still change.** Pin the exact version. The public API is locked by an
analyzer ([#63]) so that any change to it is a diff somebody reads, but locked is not the same as
frozen: the lock reports changes, it does not forbid them. 1.0 is where that promise hardens.

**What was verified after publishing rather than assumed** ([#71]): all four install from a clean
feed into a fresh project and compile against their public API; the symbol packages are on the
symbol server and the PDBs download; SourceLink resolves to the commit; the XML documentation ships
inside each package; and no analyzer or build-only package leaks into a consumer's dependency graph.
One finding — `Microsoft.Extensions.DependencyInjection.Abstractions` reaches consumers of
`.Historical` and `.Reference`, because `Microsoft.Extensions.Logging.Abstractions` declares it and
on `net10.0` it is that package's only dependency. Not removable without withdrawing the public
`LoggerFactory`.

> **Known gap, tracked as [#72]:** the packages assert `Apache-2.0` and the repository has no
> `LICENSE` file, so the README's licence badges link to a 404 and GitHub reports no licence at all.
> Being fixed before 1.0.

### Milestone 4 — Reference data ✅

*15 of 16 issues closed, [milestone](https://github.com/jerbersoft/databentodotnet/milestone/5)*

Security master, corporate actions, and adjustment factors, over streaming zstd-JSONL.

- **`ReferenceClient`** with the three sub-clients, sharing `HistoricalClient` as its transport
  ([#48])
- **`security_master.get_range` and `get_last`** ([#54]), **`corporate_actions.get_range`**
  ([#55]) and **`list_events` / `list_enums`** ([#56]), **`adjustment_factors.get_range`** ([#53])
- **An optional end to the range**, which `DateTimeRange` could not express and so did not ([#49])
- **Nineteen enums** — twelve closed with fixed wire codes ([#50]), and seven open ones that must
  carry a code they do not recognise rather than reject it ([#51])
- **The 730-member code tables are generated**, from the vendored `list_enums` output, by a script
  in the repository rather than by hand ([#58], [#59])
- **Streaming zstd-JSONL**, and the client-side sort a stream genuinely cannot do ([#52])

Still open: [#57], the opt-in tests against the real reference API — `blocked` on a reference-data
subscription, which this account does not hold.

### Milestone 3 — Historical ✅

*18 issues, [milestone](https://github.com/jerbersoft/databentodotnet/milestone/4?closed=1)*

The full historical HTTPS client: metadata, symbology, timeseries and batch.

- **`HistoricalClient`** with Basic auth, URL construction, and structured error and warning
  handling ([#35])
- **`metadata.*`** — all ten discovery and billing endpoints, including `get_cost`, which prices
  the exact request you are about to send ([#36])
- **`symbology.resolve`** ([#37]), **`timeseries.get_range` and `get_range_to_file`** ([#38]), and
  **`batch.*`** with job submission, listing, and resumable parallel download ([#39])
- **`DateRange` and `DateTimeRange`** in NodaTime, with their two distinct wire renderings ([#33])
- **`MockHistoricalGateway`** ([#34]), and opt-in tests against the real API with a second gate on
  the billable ones ([#40], [#44])
- **A real bug the mock could never have found** ([#45]): `get_dataset_condition` reads `end_date`
  as *inclusive* while `DateRange` models it as exclusive. The mock had agreed with the client
  about it for as long as both existed, because the same reading of the documentation produced
  both. Fixing it taught the lesson twice — the obvious shared fix would have broken
  `list_datasets`, which turned out to be genuinely half-open, so the endpoint being changed was
  probed rather than the one next to it ([#46]).

**[#32] moved public types between assemblies.** Code that referenced `DatabentoDotNet.Live`'s
`Symbols` or `ApiKey` needs `DatabentoDotNet.Dbn` instead. The namespace is unchanged, so for most
callers this is a project reference, not a source change.

### Milestone 2 — Live streaming ✅

*17 issues, [milestone](https://github.com/jerbersoft/databentodotnet/milestone/3?closed=1)*

A complete live-gateway client: connect, CRAM handshake, subscribe, start a session, and read
records with no allocation per record.

- **`LiveClient`** with the full session lifecycle — `ConnectAsync`, `AuthenticateAsync`,
  `SubscribeAsync`, `StartAsync`, `ReconnectAsync`, `ResubscribeAsync`, `CloseAsync` ([#19],
  [#20], [#21], [#22], [#23])
- **Two record loops.** `FillBufferAsync` + `TryNextRecord` for zero-copy, `RecordsAsync()` for an
  `await foreach` over heap copies ([#22])
- **Subscriptions** with 500-symbol chunking, `is_last` framing, and client-side validation that
  rejects a bad combination before anything reaches the socket ([#21])
- **Heartbeats, read timeouts, and slow-reader behaviour**, with `EffectiveReadTimeout` exposed so
  the derived budget can be read back ([#23])
- **NodaTime throughout**, enforced by an analyzer that fails the build on any BCL date/time type
  ([#17])
- **`RecordRef.IndexTs`** — the correct per-schema index timestamp, so a symbol lookup cannot
  silently key on `ts_event` ([#14])
- **`ISymbolIndex`** — resolve a symbol for any decoded record without knowing which map is
  answering ([#13])
- **The async read seam** decided before the socket loop was written: `SpaceMemory()` over a
  `MemoryManager<byte>`, not `System.IO.Pipelines` ([#15])
- **`MockLiveGateway`**, ported from upstream's harness and landed *before* the client ([#18])
- **Opt-in real-gateway tests** with two independent gates, so no test starts a billable session
  without its own opt-in ([#25])
- **The zero-allocation guarantee is measured**, not asserted-to — including a test that the
  measurement itself notices a deliberate allocation ([#28])

### Milestone 1 — DBN codec ✅

*8 issues, [milestone](https://github.com/jerbersoft/databentodotnet/milestone/2?closed=1)*

- **Twenty-one record structs**, every one with its `WireSize` asserted against the
  `static_assert` values in `databento-cpp` ([#3])
- **Enums and publisher tables**, with numeric validators for `Publisher`, `Dataset`, and `Venue`
  ([#2], [#11])
- **Metadata decode and encode** ([#4])
- **The incremental decoder** — `AlignedBuffer` over a `ulong[]` for guaranteed 8-byte alignment,
  and the state machine over it ([#5])
- **`TsSymbolMap` and `PitSymbolMap`** for `instrument_id` ↔ symbol resolution ([#6])
- **The `net11.0` target dropped** until .NET 11 is GA, because it was compiled nowhere ([#16])

Conformance: every `.dbn`, `.dbn.zst`, and `.dbn.frag` fixture in the vendored corpus (71 files
from `databento/dbn` 0.68.0) decodes, and yields the record counts upstream reports.

### Milestone 0 — Foundation ✅

*1 issue* — solution layout, CI across Linux, macOS, and Windows, and packaging ([#1]).

---

## In progress — Milestone 5, polish and 1.0

*21 of 23 issues closed, [milestone](https://github.com/jerbersoft/databentodotnet/milestone/6)*

Landed: the public API lock ([#63]), Native AOT verified by publishing and *running* a native binary
rather than by the analyzers alone ([#64]), four runnable samples ([#66]), the verification of the
published packages ([#71]), and this page ([#73]).

A documentation site was built ([#67]), cut to the API reference ([#69]) and retired ([#70]) inside
a single evening, then resurfaced. [#82] settled it the other way: **the site is the documentation.**
The wiki's ten guides moved into `docs/`, the wiki was retired, and there is still exactly one copy
of each fact — which is the rule all three of those issues were actually arguing about. What the
first evening got wrong was not that rule but the assumption that a second surface must mean a
second copy.

The API reference is still generated from the XML doc comments `dotnet pack` ships inside each
package, so it reaches IntelliSense at the call site and cannot drift from the code. The site
renders those comments; it does not restate them.

Also landed: the `LICENSE` file ([#72]) — the repository asserted Apache-2.0 in four published
packages while containing no copy of it, and GitHub's API reported `"license": null`. The text is now
in the repository *and* inside every package, alongside the SPDX expression rather than instead of
it.

The `0.9.0` beta shipped ([#74]) — see its section above. Its one task that no commit could do is
done too: the **`DatabentoDotNet` ID prefix is reserved**, granted 2026-08-31 and exclusive to owner
`jerbersoft`. CLAUDE.md's naming rule exists because `Databento.*` is the vendor's and unreserved;
ours is now not, and all four packages carry the reserved-prefix indicator on nuget.org.

`0.9.1` followed a day later ([#85]) and is the same code — it exists because retiring the wiki left
the four `0.9.0` package pages pointing at it, and a package page is frozen at pack time. That is
worth stating as the general rule it is: **anything a consumer reads from inside the package —
the README, the XML documentation, `projectUrl`, `releaseNotes` — can only be corrected by
publishing again.** The release checklist below now names the four `src/*/README.md` explicitly for
that reason; it previously said to grep `docs/`, which does not reach them.

The live end-to-end latency benchmark is measured ([#65]). Against `EQUS.MINI` `trades` on eight
liquid US equities on 2026-08-31: **2,240 records over five minutes**, of which this library's own
share — decoding a record and handing it to the caller — is **7.7 µs at p50 and 27 µs at p99**. That
is the figure a consumer can act on, and it reproduced to 0.1 µs across two runs on different samples
because both of its stamps come from one stopwatch: no epoch is read, so no clock offset can enter.

**The row that spans two machines' clocks came back negative through its median, and chasing that
found a better measurement** ([#83]). A gateway-to-caller figure subtracts our wall clock from
Databento's, so it carries the distance between the two clocks' zeros — 63 ms on the day. That is not
a bug: one-way delay between unsynchronised clocks is not observable at all. A round trip is, so #83
times the TCP handshake on our own stopwatch alone and gets **74.6 ms**, or 37.3 ms one way, against
~40 ms from the offset-corrected figure — two independent routes to the same answer, one of which
reads no clock. It is free to run, because a handshake completes long before a session starts.

The practical consequence for anyone reading the report: the gateway-to-caller row is a property of
how far you sit from the venue, not of this library. ROADMAP.md §7 has both tables.

Still open:

- [#68] — `0.x` → `1.0.0`. Its mechanism and metadata are done and 0.9.0 proved both on a real
  release; what is left is the promise, and the evidence for it can only come from the beta.

[#40]: https://github.com/jerbersoft/databentodotnet/issues/40
[#74]: https://github.com/jerbersoft/databentodotnet/issues/74
[#75]: https://github.com/jerbersoft/databentodotnet/issues/75
[#76]: https://github.com/jerbersoft/databentodotnet/issues/76
[#77]: https://github.com/jerbersoft/databentodotnet/issues/77
[#82]: https://github.com/jerbersoft/databentodotnet/issues/82
[#44]: https://github.com/jerbersoft/databentodotnet/issues/44
[#45]: https://github.com/jerbersoft/databentodotnet/issues/45
[#46]: https://github.com/jerbersoft/databentodotnet/issues/46
[#48]: https://github.com/jerbersoft/databentodotnet/issues/48
[#49]: https://github.com/jerbersoft/databentodotnet/issues/49
[#50]: https://github.com/jerbersoft/databentodotnet/issues/50
[#51]: https://github.com/jerbersoft/databentodotnet/issues/51
[#52]: https://github.com/jerbersoft/databentodotnet/issues/52
[#53]: https://github.com/jerbersoft/databentodotnet/issues/53
[#54]: https://github.com/jerbersoft/databentodotnet/issues/54
[#55]: https://github.com/jerbersoft/databentodotnet/issues/55
[#56]: https://github.com/jerbersoft/databentodotnet/issues/56
[#57]: https://github.com/jerbersoft/databentodotnet/issues/57
[#58]: https://github.com/jerbersoft/databentodotnet/issues/58
[#59]: https://github.com/jerbersoft/databentodotnet/issues/59
[#63]: https://github.com/jerbersoft/databentodotnet/issues/63
[#64]: https://github.com/jerbersoft/databentodotnet/issues/64
[#65]: https://github.com/jerbersoft/databentodotnet/issues/65
[#83]: https://github.com/jerbersoft/databentodotnet/issues/83
[#66]: https://github.com/jerbersoft/databentodotnet/issues/66
[#67]: https://github.com/jerbersoft/databentodotnet/issues/67
[#68]: https://github.com/jerbersoft/databentodotnet/issues/68
[#69]: https://github.com/jerbersoft/databentodotnet/issues/69
[#70]: https://github.com/jerbersoft/databentodotnet/issues/70
[#71]: https://github.com/jerbersoft/databentodotnet/issues/71
[#72]: https://github.com/jerbersoft/databentodotnet/issues/72
[#73]: https://github.com/jerbersoft/databentodotnet/issues/73
[#78]: https://github.com/jerbersoft/databentodotnet/issues/78
[#85]: https://github.com/jerbersoft/databentodotnet/issues/85
[#1]: https://github.com/jerbersoft/databentodotnet/issues/1
[#2]: https://github.com/jerbersoft/databentodotnet/issues/2
[#3]: https://github.com/jerbersoft/databentodotnet/issues/3
[#4]: https://github.com/jerbersoft/databentodotnet/issues/4
[#5]: https://github.com/jerbersoft/databentodotnet/issues/5
[#6]: https://github.com/jerbersoft/databentodotnet/issues/6
[#11]: https://github.com/jerbersoft/databentodotnet/issues/11
[#13]: https://github.com/jerbersoft/databentodotnet/issues/13
[#14]: https://github.com/jerbersoft/databentodotnet/issues/14
[#15]: https://github.com/jerbersoft/databentodotnet/issues/15
[#16]: https://github.com/jerbersoft/databentodotnet/issues/16
[#17]: https://github.com/jerbersoft/databentodotnet/issues/17
[#18]: https://github.com/jerbersoft/databentodotnet/issues/18
[#19]: https://github.com/jerbersoft/databentodotnet/issues/19
[#20]: https://github.com/jerbersoft/databentodotnet/issues/20
[#21]: https://github.com/jerbersoft/databentodotnet/issues/21
[#22]: https://github.com/jerbersoft/databentodotnet/issues/22
[#23]: https://github.com/jerbersoft/databentodotnet/issues/23
[#25]: https://github.com/jerbersoft/databentodotnet/issues/25
[#28]: https://github.com/jerbersoft/databentodotnet/issues/28
[#32]: https://github.com/jerbersoft/databentodotnet/issues/32
[#33]: https://github.com/jerbersoft/databentodotnet/issues/33
[#34]: https://github.com/jerbersoft/databentodotnet/issues/34
[#35]: https://github.com/jerbersoft/databentodotnet/issues/35
[#36]: https://github.com/jerbersoft/databentodotnet/issues/36
[#37]: https://github.com/jerbersoft/databentodotnet/issues/37
[#38]: https://github.com/jerbersoft/databentodotnet/issues/38
[#39]: https://github.com/jerbersoft/databentodotnet/issues/39

---

## The release checklist

For whoever cuts the first one. Not automated yet.

1. Every issue in the milestone is closed, and the milestone itself is closed.
2. `dotnet build` and `dotnet test` are green on all three CI platforms, with zero warnings —
   `TreatWarningsAsErrors` means a warning is already a failure.
3. Version set in `Directory.Build.props`. Drop `VersionSuffix` for a stable release.
4. Benchmarks run and the throughput and allocated-bytes numbers recorded, so a later regression
   has something to be a regression *from*.
5. `dotnet pack -c Release`, and the resulting `.nupkg` inspected — it should carry the `.snupkg`
   symbol package and SourceLink metadata.
6. Tag `v0.x.y`, push the tag, and write the GitHub Release against it. Publishing a release runs
   `publish.yml`.
7. **Confirm the run actually published.** Two things learned cutting `0.1.0-alpha`:
   `dotnet nuget push "nupkg/*.nupkg"` sends the adjacent `.snupkg` on its own, so symbols need no
   separate step — but `--skip-duplicate` means a re-run where every package already exists reports
   **success having pushed nothing**, symbols included, because a skipped primary push skips its
   symbol package too. A green tick is not evidence a version reached the feed. Read the log.
8. Install each package into a throwaway project from a clean feed and compile against it, with
   `NUGET_PACKAGES` pointed at an empty directory so nothing resolves from the local build. [#71]
   has the method.
9. Update this page with the narrative and any upgrade notes — and every other page that names a
   version. Grep the **whole repository** for the old version number rather than editing the page you
   happen to be thinking about, and do it before step 5, not after step 8.

   Three groups state the current version, and they are not all in `docs/`:

   - `docs/index.md`, `docs/guides/getting-started.md`, `docs/guides/faq.md` — these were still
     saying `0.1.0-alpha` after `0.9.0` went out.
   - `README.md` and `CONTRIBUTING.md`.
   - **`src/*/README.md`, all four.** These are the ones that matter most and are easiest to miss:
     they are `PackageReadmeFile`, so they *are* the nuget.org package pages, and once packed they
     are frozen. `0.9.1` exists because these four were left pointing at the retired wiki when
     `0.9.0` was packed, and a wrong package page can only be superseded, never edited. A grep
     scoped to `docs/` — which is what this step used to say — does not reach them.

   Leave the sections of this page describing *past* releases alone. They are a historical record;
   the figures in the `0.9.0` section are what was true of `0.9.0`.
10. Verify against the **published** artefacts, not the local pack: download each `.nupkg` back off
    the feed and read its nuspec, install into a throwaway project with `NUGET_PACKAGES` pointed at
    an empty directory, and fetch the PDBs with
    `dotnet-symbol --server-path https://symbols.nuget.org/download/symbols/`. The default server is
    Microsoft's and will report every PDB as Not Found, which looks exactly like a failed symbol
    push.

> **The licence half of this is fixed; the lesson it taught is not.** This page recommended a
> `LICENSE` file before anything was published, `0.1.0-alpha` shipped without one anyway, and four
> packages spent their first days asserting Apache-2.0 against a repository whose licence badges
> linked to a 404 and whose GitHub API entry read `"license": null`. What changed it was [#72] —
> an issue. That is the whole difference: a recommendation with nothing tracking it does not stop a
> release, which is why the checklist above is a checklist and not a paragraph.
>
> **Both follow-ons now exist too.** `SECURITY.md` ([#75]) and `CONTRIBUTING.md` ([#76]) were the
> other two files GitHub surfaces in its own UI, and they were flagged here in the same breath as the
> licence — untracked, and therefore still missing when `0.9.0` shipped. They landed the way the
> licence did: an issue first. The security one mattered most, because four public packages with no
> stated private channel means a finder's reasonable default is a public issue, which discloses the
> flaw to every consumer before a fix exists.
>
> `CODE_OF_CONDUCT.md` followed ([#77]) — Contributor Covenant 2.1, verbatim, because GitHub detects
> that file by matching known text exactly as it does a licence. **The family is complete**: GitHub's
> community profile now reports all four, where it reported one before `0.9.0`.
>
> The pattern is the point, and it repeated four times: each of these was written down as a
> recommendation, none of them shipped, and every one of them landed within an hour of getting an
> issue number. Recommendations do not stop releases. Issues do.

## See also

- [`ROADMAP.md`](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — what each milestone contains, and every design decision with its reasoning
- [Milestones](https://github.com/jerbersoft/databentodotnet/milestones) — live progress bars
- [`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) — why the changelog belongs in the repo and this page does not
