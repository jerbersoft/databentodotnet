# Fixture provenance

These 71 files are vendored verbatim from the [`databento/dbn`](https://github.com/databento/dbn)
crate's test corpus:

- **Upstream repository:** `https://github.com/databento/dbn`
- **Path:** `tests/data/`
- **Crate version:** 0.68.0 (commit `18ab77c`, "VER: Release DBN 0.68.0")
- **License:** Apache-2.0 (same license as this repository)
- **Copy method:** byte-for-byte copy (`cp -p`), verified against the source directory with
  `md5` — every vendored file is identical to its upstream counterpart. Nothing here was
  regenerated, re-encoded, or hand-edited.

## Why 71 files, not 81

The upstream directory holds 81 files. The 10 `.dbz` files were excluded on purpose: legacy
DBZ is a different container format, not merely an older DBN version, and issue #4 puts version
0 support out of scope. If DBZ support is ever wanted, it is its own issue — see
`tests/DatabentoDotNet.Dbn.Tests/TestFixtures.cs` for how the remaining 71 are loaded and
categorized.

## Why `databento-rs`, not this directory, was not the source

`databento-rs/tests/data` also ships DBN fixtures, but all 24 of them are DBN v1. Vendoring
that set instead would leave the v2 decode path, the v3 decode path, and both upgrade paths
with zero fixture coverage. The `dbn` crate's corpus (this one) covers all three wire versions
plus zstd-compressed streams and metadata-less fragments.
