# Contributing

A .NET client for [Databento](https://databento.com) market data, ported from Databento's own Rust
implementation. Contributions are welcome, and `0.10.0` is a `0.x` release specifically so the
public API can still be argued with — the hosting extensions most of all, since `0.10.0` is their
first published version.

**This file is a signpost, not the rules.** The conventions live in
[`CLAUDE.md`](CLAUDE.md) and there is deliberately only one copy of them.

## Where to go

| | |
|---|---|
| A question, or "is this supposed to work?" | [Discussions](https://github.com/jerbersoft/databentodotnet/discussions), or the [FAQ](https://jerbersoft.github.io/databentodotnet/guides/faq.html) and [Troubleshooting](https://jerbersoft.github.io/databentodotnet/guides/troubleshooting.html) pages |
| A bug, or something you want built | [Open an issue](https://github.com/jerbersoft/databentodotnet/issues/new/choose) |
| The API is awkward to use | An issue, and please do — that is what the beta is *for*, and it is far cheaper to change now than after 1.0 |
| A security vulnerability | [Privately](https://github.com/jerbersoft/databentodotnet/security/advisories/new), **never** a public issue. See [`SECURITY.md`](SECURITY.md) |
| Someone's behaviour | [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) names the contact. Not an issue either — a report about a person should not be the first thing a stranger reads |

## The one rule worth knowing before you start

**An issue exists before the work does.** Every change here begins with one — features, bugs,
chores, documentation — and commits reference it (`Fixes #12`, `Refs #12`). Issues carry a milestone,
one `type:` label and at least one `area:` label, and open with a BLUF: one or two sentences at the
top saying what ships and why.

This is stated here because it is the rule that wastes your time if you meet it late. A finished pull
request with no issue behind it has been done in the wrong order, and unpicking that afterwards is
worse for you than for anyone else. Open the issue first — it can be two lines, and it is where the
"should this exist at all" conversation happens while it is still cheap.

## Building it

```sh
dotnet build
dotnet test
```

The .NET 10 SDK or newer. That is the whole setup — there is nothing to install, no code generation
step in the build, and no local service to run. CI runs the same two commands on Linux, macOS and
Windows, plus a workflow that publishes and *runs* a Native AOT binary.

`dotnet test` runs everything that does not need credentials. The tests that talk to Databento are
filtered out by category and gated on environment variables, and the ones that would spend money
carry a second gate on top of the first. `CLAUDE.md` has the table; the short version is that
nothing bills you by accident.

## Why the contributor guide is written for an AI assistant

Because that is who does most of the work in this repository, and pretending otherwise would produce
two guides that disagree. `CLAUDE.md` is the operating guide, its conventions are the project's
conventions regardless of who is reading, and a human contributor loses nothing by reading a document
addressed to someone else.

## Where everything is written down

Four documents, and each fact lives in exactly one of them. Please keep it that way — this repository
has already grown a duplicate documentation set once and deleted it again.

- [`CLAUDE.md`](CLAUDE.md) — conventions, workflow, labels, testing gates, layout
- [`ROADMAP.md`](ROADMAP.md) — milestones, architecture, and every design decision with its reasoning
- [`PORTING.md`](PORTING.md) — how the Rust source maps to .NET, and what deliberately does not port
- [`docs/`](docs) — the guides and release notes, published to
  [jerbersoft.github.io/databentodotnet](https://jerbersoft.github.io/databentodotnet/)

The API reference is generated from the XML documentation comments, which `dotnet pack` ships inside
each package — so it reaches IntelliSense at the call site *and* renders on the site, from one
source that cannot drift.

**Documentation lives in `docs/`, not in a wiki.** There was a wiki until
[#82](https://github.com/jerbersoft/databentodotnet/issues/82), and moving its pages into the
repository is what lets a behaviour change and the page describing it land in the same pull request.
The site builds with `--warningsAsErrors`, so a cross-reference that stops resolving fails CI
instead of rotting quietly. If you change behaviour, change its guide in the same commit.
