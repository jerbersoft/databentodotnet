# Fixture provenance

These two files are the **live Databento reference API's own responses**, captured verbatim by this
repository. They are the oracle #50 and #51 transcribe their enum tables from.

- **Endpoint host:** `https://hist.databento.com` (`HistoricalGateway.Bo1`)
- **Requests:** `GET /v0/corporate_actions.list_enums` and `GET /v0/corporate_actions.list_events`,
  HTTP Basic with an API key as the username and an empty password, no query parameters
- **Captured:** 2026-08-29T08:03:19Z, both in the same run
- **Response:** `200 OK`, `content-type: application/json`, no `X-Warning`, no
  `content-encoding` — the bodies are plain JSON, not zstd-framed. (The `get_range` endpoints are
  the zstd-framed ones; these two are not.)
- **Copy method:** byte-for-byte, `cp -p`, md5-verified against the captured responses. Nothing here
  was pretty-printed, key-ordered, pruned, or hand-edited. Both arrived minified — one line, no
  indentation — and are stored that way; `ReferenceEnumFixtureTests` asserts it, because a
  format-on-save is the realistic way "byte-for-byte" stops being true.
- **MD5, as captured:**
  - `df94ab89339bef1ee11dc522b24fefa0`  `corporate_actions.list_enums.json`
  - `8cfc36aa3e06971d09ed0ce67153112e`  `corporate_actions.list_events.json`
- **Credentials:** none. These are response bodies only; the key travelled in an `Authorization`
  header that is not part of a response and is not recorded anywhere in this repository.

## These are not upstream fixtures

Everywhere else in this repository "vendored" means copied from a Databento-authored repository —
`tests/DatabentoDotNet.Dbn.Tests/Data/` holds 71 files taken from the `databento/dbn` crate's own
test corpus. These two are different: they came off the wire from the production API, and no
upstream repository contains them. That distinction is the reason they are worth having.

## Why capture them at all

`databento-rs` models nineteen reference enums in `src/reference/enums.rs`, and comparing them
against what the API actually reports found it behind on three:

| enum | modelled upstream | server reports | |
|---|---|---|---|
| `SecurityType` | 30 | 64 (`SECTYPE`) | and non-optional on `AdjustmentFactor` |
| `Frequency` | 14 | 16 (`FREQ`) | `BIW`, `FRT` missing |
| `Event` | 60 | 141 (`EVENT`) | and stale in *both* directions — see below |

`Event` is the instructive one. Upstream carries `DIVEB` and `LTCHG`, which `list_events` does not
document; `list_events` documents `DIVIF` and `MFCON`, which upstream lacks. All four exist in the
`EVENT` dictionary group. A port that transcribed `enums.rs` would inherit every one of these, and
nothing in the port would reveal it.

The eight char-coded enums the dictionary covers — `ACTION`, `FRACCD`, `GLOBSTATUS`, `LISTSOURCE`,
`LISTSTAT`, `MANDVOLU`, `PAYTYPE`, `VOTING` — match upstream exactly. That contrast is what drew
the line between #50 and #51 where it now sits: wire alphabet versus data dictionary.

## What is in them

`corporate_actions.list_enums.json` is a JSON object keyed by enum group name, each value an array
of `{code, description}` where `code` may be `null` ("a blank value is possible"). It is the
**corporate actions data dictionary**, far broader than the ten enums this library types: 235 groups
and 13,123 entries, of which `ETFBNCH` alone is 7,705. That breadth is expected and is not a gap to
close — it is also why `CorporateAction`'s `date_info`, `rate_info` and `event_info` stay open maps
rather than becoming typed models.

Two shapes worth knowing before writing an assertion against it:

- **A group may repeat a code.** `EVENTSUBTYPE` has 80 entries but only 67 distinct codes; six codes
  appear more than once with a description that depends on the parent event. Deduplicate by code.
- **A null code is a value, not a hole.** `EVENTSUBTYPE` has seven ("Generic event, no subtype
  provided"), and `SECTYPE`, `FREQ`, `FRACCD` and `PAYTYPE` have one each. It means blank is legal
  for that field, which is why the corresponding model fields are nullable.

`corporate_actions.list_events.json` is a JSON object keyed by event code — 60 of them, each an
`EventDoc` with `calendar_dates`, `category`, `code`, `description`, `fields`, `level`, `name`,
`participation` and `subtypes`. It is the **only** authority for three enums that have no
`list_enums` group at all: `EventCategory` (8 values), `EventLevel` (4) and `FieldGroup` (3). All
three match upstream exactly.

## The rule these files exist under

**Nothing may parse them with this library's own reference models or JSON converters.** Those are
the code these fixtures exist to check, and an oracle read by the code it checks is not an oracle —
the same argument `tests/DatabentoDotNet.Historical.Tests/BannedSymbols.txt` makes for
`MetadataEncoder`, and the same one that keeps `MockHistoricalGateway` from using the client it
tests. `ReferenceEnumFixture` reads them with `System.Text.Json`'s `JsonDocument` and hands back
plain dictionaries.

## Re-capturing

If Databento's dictionary changes and these need refreshing:

1. Re-run both requests against `https://hist.databento.com/v0/`. Both are discovery endpoints and
   cost nothing — reference *data* is billable, this documentation is not. Keep the credential out
   of `argv`; see `RealHistoricalApiTests` for how this repository does that.
2. Replace both files verbatim. Do not reformat.
3. Update the capture date and the counts above, then run the test suite. `ReferenceEnumFixtureTests`
   asserts the group, entry and event counts, so a changed dictionary fails loudly here rather than
   silently widening what #50 and #51 believe.
4. Expect the enum tables in #50 and #51 to need updating too. That is the point of the exercise:
   a new code in the dictionary is a real change, and this is where it surfaces.
