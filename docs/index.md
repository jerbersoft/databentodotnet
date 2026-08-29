---
_layout: landing
---

# DatabentoDotNet API reference

Every public member of the four packages, generated from the XML documentation in the source.

**This site is the API reference and deliberately nothing else.** Guides, explanations and
troubleshooting live in the [wiki](https://github.com/jerbersoft/databentodotnet/wiki); repository
conventions live in
[`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md). The wiki's own
[style guide](https://github.com/jerbersoft/databentodotnet/wiki/Wiki-Style-Guide) draws that line
and gives the reason: one canonical location per fact, because the second copy is the one that goes
stale.

> [!NOTE]
> This is a third-party client. It is not published or endorsed by Databento.

## Start here

| If you want to | Go to |
|---|---|
| Look up a type, member, or overload | [API reference](api/index.md) |
| Learn the library, or understand a design decision | [The wiki](https://github.com/jerbersoft/databentodotnet/wiki) |
| Know what a `RecordRef` may outlive | [Zero-Copy and Allocation](https://github.com/jerbersoft/databentodotnet/wiki/Zero-Copy-and-Allocation) |
| Know why nothing here takes a `DateTime` | [Timestamps and Prices](https://github.com/jerbersoft/databentodotnet/wiki/Timestamps-and-Prices) |
| Run something | [The four samples](https://github.com/jerbersoft/databentodotnet/tree/master/samples) |
| Contribute | [`CLAUDE.md`](https://github.com/jerbersoft/databentodotnet/blob/master/CLAUDE.md) |

## The four packages

| Package | Contents |
|---|---|
| `DatabentoDotNet.Dbn` | The DBN codec — record structs, metadata, decoder, symbol maps |
| `DatabentoDotNet.Live` | Real-time and intraday-replay streaming over the raw TCP gateway |
| `DatabentoDotNet.Historical` | Historical HTTPS API — timeseries, batch, symbology, metadata |
| `DatabentoDotNet.Reference` | Security master, corporate actions, adjustment factors |

`DatabentoDotNet.Dbn` is the only one with no sibling dependency; each of the other three brings it
in.

## Why the reference is complete

`GenerateDocumentationFile` and `TreatWarningsAsErrors` are both on for all four projects, so a
public member without a documentation comment has never compiled in this repository. There is no
undocumented corner to find, and a broken `<see cref>` is a build error rather than a bare word on
a page.
