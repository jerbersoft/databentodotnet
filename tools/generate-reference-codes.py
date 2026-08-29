#!/usr/bin/env python3
"""Generates the ten DatabentoDotNet.Reference open code types -- Country, Currency, Event,
EventSubType, SecurityType, Frequency, OutturnStyle, EventCategory, EventLevel, FieldGroup --
from the vendored `corporate_actions.list_enums` and `corporate_actions.list_events` responses.

Why a generator and not a hand-written table: the ten types carry 730 static members between
them, and every one of those members is a code out of Databento's data dictionary rather than an
identifier this library chose. Hand-transcribing 730 codes and their descriptions would, with
near certainty, introduce an error no reviewer would spot; and the dictionary grows, so the job
recurs on every fixture re-capture. `databento-rs` hand-maintains the equivalent tables in
`enums.rs`, which is exactly how it came to be 34 codes behind on `SecurityType` and 2 behind on
`Frequency` -- the drift #58 found by asking the API. A generator plus a vendored response is the
answer to that failure mode rather than a transcription of it.

Why not a Roslyn source generator, which would remove the checked-in output entirely:

  1. It would make ReferenceCodeTableTests vacuous. That test compares the shipped members
     against the vendored fixture; emitting the members from that same fixture at build time
     would have it compare the file to itself, and it could never fail.
  2. It would let a fixture re-capture change the public API silently -- including *removing*
     public members, which is a breaking change. Committed output means the API moves only when
     a human runs this script and reads the diff.

Usage:
    python3 tools/generate-reference-codes.py <path-to-fixture-dir> [--out-dir DIR]

    <path-to-fixture-dir> holds corporate_actions.list_enums.json and
    corporate_actions.list_events.json -- normally
    tests/DatabentoDotNet.Reference.Tests/Data. It is always a required argument; this script
    never hard-codes a path to the fixtures.

Failure mode: every structural expectation below is a hard assertion -- the groups that must be
present, the identifier rule producing no collisions, the member count matching the distinct
codes parsed. If the fixtures change shape in a way this script does not recognise, it prints a
diagnostic to stderr and exits non-zero *without writing any output file*. Every file is
rendered in full before the first one is written, so a partial or silently-wrong generation is
not a state this script can leave behind.

The check that this script and the tables agree is `git diff` being empty after a run, and
ReferenceCodeTableTests is the separate check that both agree with the server.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import OrderedDict
from pathlib import Path
from typing import Callable, Dict, List, Optional, Sequence, Tuple

GENERATOR_RELATIVE_PATH = "tools/generate-reference-codes.py"

ENUMS_FILE = "corporate_actions.list_enums.json"
EVENTS_FILE = "corporate_actions.list_events.json"

# Members that would collide with the shape every generated type carries.
RESERVED = frozenset(
    {"Code", "From", "HasValue", "IsKnown", "KnownCodes", "Equals", "GetHashCode", "ToString"}
)


class GeneratorError(RuntimeError):
    """Raised when the fixtures do not match the shape this script knows how to read."""


def fail(message: str) -> None:
    raise GeneratorError(message)


# ------------------------------------------------------------------------------------- Naming


def pascal_code(code: str) -> str:
    """The rule nine of the ten types use: the PascalCase of the wire code.

    This reproduces upstream's variant names exactly -- all 246 countries, all 179 currencies,
    all 67 sub-types, zero mismatches -- which is the check that the rule is upstream's rather
    than this port's invention.
    """
    return code[0].upper() + code[1:].lower()


def pascal_words(value: str) -> str:
    """PascalCase of a snake_cased or hyphenated value, for the three list_events vocabularies."""
    return "".join(p[:1].upper() + p[1:] for p in re.split(r"[^A-Za-z0-9]+", value) if p)


def frequency_name(code: str, descriptions: Sequence[str]) -> str:
    """Frequency alone names its members after their descriptions, because upstream does.

    Upstream falls back to the code where the description is more than one word, which is why
    INTONMAT and ITM -- which share the description "Interest on Maturity" and would otherwise
    collide -- keep their codes. BIW and FRT, the two codes upstream lacks, fall out of that same
    rule as BiWeekly and Fortnightly rather than being invented here.
    """
    description = descriptions[0] if descriptions else ""
    if description and len(re.split(r"\s+", description.strip())) == 1:
        return pascal_words(description)
    return pascal_code(code)


# -------------------------------------------------------------------------------- The ten types

# `extra` and `extra2` are the type-specific paragraphs in the XML remarks. Everything else about a type's
# documentation is shared, so it lives in render_type below rather than being repeated ten times.
TYPES: List[Dict] = [
    dict(
        name="Country",
        group="CNTRY",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:137",
        summary="A country code — ISO 3166-1 alpha-2, with unofficial extensions Databento adds.",
        extra="<c>ZZ</c> is one of the extensions and means <em>Unclassified</em>; it is a known "
        "code, not the absence of one.",
    ),
    dict(
        name="Currency",
        group="CUREN",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:1169",
        summary="A currency code — ISO 4217 alpha-3.",
        extra=None,
    ),
    dict(
        name="Event",
        group="EVENT",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:1933",
        summary="A corporate-action event type.",
        extra="Seeded from the <c>EVENT</c> dictionary group's 141 codes rather than from the 60 "
        "events <c>corporate_actions.list_events</c> documents. The 141 are a strict superset and "
        "are the widest vocabulary a record's <c>event</c> field is known to carry; the documented "
        "60 are the subset with published field lists, not the type's range.",
        extra2="<b>The name is <c>Event</c> even though that word is reserved in Visual Basic.</b> It "
        "is what upstream calls this (<c>enums.rs:1933</c>), what the wire field is called, and the "
        "name a reader arrives with from the API documentation or the Rust client. A VB consumer "
        "writes <c>[Event]</c> to escape it, which is a known and minor cost; renaming to "
        "<c>EventType</c> would buy that back and spend the far larger one. The analyzer that "
        "objects to it, CA1716, is silent here only because this file is generated — hence the "
        "reason living in the documentation, where it is read, rather than in a suppression that "
        "suppresses nothing.",
    ),
    dict(
        name="EventSubType",
        group="EVENTSUBTYPE",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:2365",
        summary="A corporate-action event sub-type.",
        extra="The dictionary carries 80 entries for 67 distinct codes: six codes appear more than "
        "once with a description that depends on the parent event, and seven entries carry no code "
        "at all. The members here are deduplicated by code, and a member whose code has more than "
        "one description names all of them — the description belongs to the event, not to the "
        "sub-type.",
    ),
    dict(
        name="SecurityType",
        group="SECTYPE",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:3281",
        summary="The type of a security.",
        extra="Upstream models 30 of these and the live dictionary reports 64, which is why this is "
        "an open carrier rather than a closed enum. The consequence is not cosmetic: upstream types "
        "<c>AdjustmentFactor::security_type</c> as a bare <c>SecurityType</c> rather than an "
        "<c>Option</c> (<c>adjustment.rs:109</c>), so one of the 34 codes it does not model fails "
        "the whole row rather than one field.",
    ),
    dict(
        name="Frequency",
        group="FREQ",
        namer=frequency_name,
        rust="enums.rs:2799",
        summary="How often a distribution recurs.",
        extra="The one type here whose members are named after their descriptions rather than their "
        "codes, because upstream names them that way. Upstream falls back to the code where the "
        "description is more than one word, which is why <see cref=\"Intonmat\"/> and "
        "<see cref=\"Itm\"/> — which share the description \"Interest on Maturity\" — keep their "
        "codes. <see cref=\"BiWeekly\"/> and <see cref=\"Fortnightly\"/> are the two the live "
        "dictionary has and upstream does not; both names fall out of that same rule.",
    ),
    dict(
        name="OutturnStyle",
        group="OUTTURNSTYLE",
        namer=lambda c, d: pascal_code(c),
        rust="enums.rs:3157",
        summary="Whether an outturn security is new or additional to an existing holding.",
        extra="Exact against the live dictionary today, at two codes each, and an open carrier "
        "anyway: the rule is where a vocabulary comes from, not how many values it currently holds.",
    ),
    dict(
        name="EventCategory",
        events="category",
        namer=lambda c, d: pascal_words(c),
        rust="enums.rs:2221",
        summary="The category a corporate-action event falls into.",
        extra="<see cref=\"Other\"/> is a value the server sends and is not the same thing as an "
        "unrecognised code. A code the server adds later is carried intact and reports "
        "<see cref=\"IsKnown\"/> <see langword=\"false\"/>; <c>other</c> is known and means "
        "<em>other</em>.",
    ),
    dict(
        name="EventLevel",
        events="level",
        namer=lambda c, d: pascal_words(c),
        rust="enums.rs:2301",
        summary="The level a corporate-action event applies at.",
        extra=None,
    ),
    dict(
        name="FieldGroup",
        events="field_group",
        namer=lambda c, d: pascal_words(c),
        rust="enums.rs:2681",
        summary="Which of a corporate action's three open field maps a field belongs to.",
        extra="Names <c>event_info</c>, <c>date_info</c> and <c>rate_info</c> — the three maps "
        "<c>CorporateAction</c> carries (<c>corporate.rs:433-438</c>). Load-bearing for the "
        "<c>list_events</c> documentation as well as for a response field.",
    ),
]


# ------------------------------------------------------------------------------------- Parsing


def load_fixtures(data_dir: Path) -> Tuple[Dict, Dict]:
    for name in (ENUMS_FILE, EVENTS_FILE):
        if not (data_dir / name).is_file():
            fail(f"{data_dir / name} is missing; expected the vendored {name}")

    enums = json.loads((data_dir / ENUMS_FILE).read_text(encoding="utf-8"))
    events = json.loads((data_dir / EVENTS_FILE).read_text(encoding="utf-8"))

    if not isinstance(enums, dict) or not enums:
        fail(f"{ENUMS_FILE} is not the object of groups this generator expects")
    if not isinstance(events, dict) or not events:
        fail(f"{EVENTS_FILE} is not the object of events this generator expects")

    return enums, events


def codes_from_group(enums: Dict, group: str) -> List[Tuple[str, List[str]]]:
    """A group's distinct codes, each with its distinct descriptions, in code order.

    Distinct because a group may repeat a code -- EVENTSUBTYPE has 80 entries and 67 codes. Codes
    that are null or empty are dropped: a blank is the absence of a value rather than a member,
    and 148 of the 235 groups carry one.
    """
    if group not in enums:
        fail(f"{ENUMS_FILE} has no group named {group}")

    entries = enums[group]
    if not isinstance(entries, list) or not entries:
        fail(f"group {group} is not the non-empty list of variants this generator expects")

    by_code: "OrderedDict[str, List[str]]" = OrderedDict()
    for entry in entries:
        if not isinstance(entry, dict) or "code" not in entry:
            fail(f"group {group} holds an entry with no 'code' key")
        code = entry["code"]
        if not code:
            continue
        description = (entry.get("description") or "").strip()
        by_code.setdefault(code, [])
        if description and description not in by_code[code]:
            by_code[code].append(description)

    if not by_code:
        fail(f"group {group} yielded no codes at all")

    return [(code, by_code[code]) for code in sorted(by_code)]


def codes_from_events(events: Dict, field: str) -> List[Tuple[str, List[str]]]:
    """The distinct values of one list_events vocabulary.

    list_enums has no group for EventCategory, EventLevel or FieldGroup, so list_events is their
    only authority.
    """
    values = set()
    for code, doc in events.items():
        if not isinstance(doc, dict):
            fail(f"{EVENTS_FILE} entry {code} is not an object")
        if field == "field_group":
            fields = doc.get("fields")
            if not isinstance(fields, list):
                fail(f"{EVENTS_FILE} entry {code} has no 'fields' list")
            for item in fields:
                if not isinstance(item, dict) or "group" not in item:
                    fail(f"{EVENTS_FILE} entry {code} has a field with no 'group'")
                values.add(item["group"])
        else:
            if field not in doc:
                fail(f"{EVENTS_FILE} entry {code} has no '{field}'")
            values.add(doc[field])

    if not values:
        fail(f"{EVENTS_FILE} yielded no values for {field}")

    return [(value, []) for value in sorted(values)]


# ------------------------------------------------------------------------------------ Rendering


def xml_escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def header() -> List[str]:
    return [
        "// <auto-generated>",
        f"//   Generated by {GENERATOR_RELATIVE_PATH} from the vendored reference API responses",
        f"//   ({ENUMS_FILE} and {EVENTS_FILE}). Do not hand-edit.",
        f"//   Regenerate with: python3 {GENERATOR_RELATIVE_PATH} tests/DatabentoDotNet.Reference.Tests/Data",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "using System.Collections.Frozen;",
        "using System.Text.Json.Serialization;",
        "using DatabentoDotNet.Reference.Json;",
        "",
        "namespace DatabentoDotNet.Reference;",
        "",
    ]


def render_type(spec: Dict, items: List[Tuple[str, List[str]]]) -> str:
    name = spec["name"]
    namer: Callable[[str, Sequence[str]], str] = spec["namer"]

    members: List[Tuple[str, str, List[str]]] = []
    seen: Dict[str, str] = {}
    for code, descriptions in items:
        identifier = namer(code, descriptions)
        if identifier in RESERVED:
            fail(f"{name}: code {code} yields {identifier}, which collides with a shared member")
        if identifier in seen:
            fail(f"{name}: codes {seen[identifier]} and {code} both yield the identifier {identifier}")
        seen[identifier] = code
        members.append((identifier, code, descriptions))

    if len(members) != len(items):
        fail(f"{name}: rendered {len(members)} members for {len(items)} codes")

    if spec.get("events"):
        authority = (
            "<c>corporate_actions.list_enums</c> has no group for this type, so "
            "<c>corporate_actions.list_events</c> is its only authority — and the two agree "
            "exactly, upstream included."
        )
    else:
        authority = (
            f"The members come from the <c>{spec['group']}</c> group of the vendored "
            f"<c>corporate_actions.list_enums</c> response, which is the oracle rather than a "
            f"count typed into an issue."
        )

    lines: List[str] = header()
    lines += [
        "/// <summary>",
        f"/// {spec['summary']}",
        "/// </summary>",
        "/// <remarks>",
        "/// <para>",
        "/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends",
        f"/// this enum in an <c>Unknown(String)</c> variant (<c>{spec['rust']}</c>) so a code Databento adds",
        "/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See",
        "/// <see cref=\"IReferenceCode{TSelf}\"/> for the shape this takes instead and why.",
        "/// </para>",
        "/// <para>",
        f"/// {authority}",
        "/// </para>",
    ]
    for paragraph in (spec.get("extra"), spec.get("extra2")):
        if paragraph:
            lines += ["/// <para>", f"/// {paragraph}", "/// </para>"]
    lines.append("/// </remarks>")

    lines += [
        f"[JsonConverter(typeof(ReferenceCodeJsonConverter<{name}>))]",
        f"public readonly record struct {name} : IReferenceCode<{name}>",
        "{",
        "    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(",
        "    [",
    ]
    lines += [f'        "{code}",' for _, code, _ in members]
    lines += [
        "    ], StringComparer.Ordinal);",
        "",
        "    private readonly string? _code;",
        "",
        "    /// <summary>",
        "    /// Wraps a wire code, known or not. Prefer a named member such as",
        f"    /// <see cref=\"{members[0][0]}\"/> where one exists, and <see cref=\"From\"/> where the value came",
        "    /// from the server.",
        "    /// </summary>",
        '    /// <param name="code">The wire code.</param>',
        '    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>',
        f"    public {name}(string code)",
        "    {",
        "        ArgumentException.ThrowIfNullOrEmpty(code);",
        "        _code = code;",
        "    }",
        "",
        "    /// <summary>",
        "    /// Every code the reference API reported for this type when the fixture was captured —",
        f"    /// {len(members)} of them.",
        "    /// </summary>",
        "    public static IReadOnlySet<string> KnownCodes => Codes;",
        "",
        "    /// <inheritdoc/>",
        "    public string? Code => _code;",
        "",
        "    /// <inheritdoc/>",
        "    public bool HasValue => _code is not null;",
        "",
        "    /// <inheritdoc/>",
        "    public bool IsKnown => _code is not null && Codes.Contains(_code);",
        "",
        "    /// <summary>",
        '    /// Reads a wire code, mapping <see langword="null"/> and the empty string to',
        '    /// <see langword="default"/> — the absence of a value.',
        "    /// </summary>",
        '    /// <param name="code">The wire code, or <see langword="null"/>.</param>',
        "    /// <returns>The value.</returns>",
        f"    public static {name} From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);",
        "",
        "    /// <summary>The wire code, or the empty string when this names no value.</summary>",
        "    /// <returns>The wire code.</returns>",
        "    public override string ToString() => _code ?? string.Empty;",
    ]

    for identifier, code, descriptions in members:
        doc = " / ".join(xml_escape(d) for d in descriptions) if descriptions else xml_escape(code)
        lines += [
            "",
            f"    /// <summary>{doc} (<c>{xml_escape(code)}</c>).</summary>",
            f'    public static {name} {identifier} => new("{code}");',
        ]

    lines.append("}")
    return "\n".join(lines) + "\n"


# ------------------------------------------------------------------------------------------ Main


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "data_dir",
        type=Path,
        help=f"Directory holding {ENUMS_FILE} and {EVENTS_FILE}",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=None,
        help="Output directory (default: <repo>/src/DatabentoDotNet.Reference, relative to this script)",
    )
    args = parser.parse_args(argv)

    if not args.data_dir.is_dir():
        print(f"error: {args.data_dir} is not a directory", file=sys.stderr)
        return 1

    # Every file is rendered before any is written, so a failure anywhere leaves the tree alone.
    try:
        enums, events = load_fixtures(args.data_dir)
        files: "OrderedDict[str, str]" = OrderedDict()
        counts: List[Tuple[str, int]] = []
        for spec in TYPES:
            if spec.get("events"):
                items = codes_from_events(events, spec["events"])
            else:
                items = codes_from_group(enums, spec["group"])
            files[f"{spec['name']}.cs"] = render_type(spec, items)
            counts.append((spec["name"], len(items)))
    except GeneratorError as exc:
        print(f"error: {exc}", file=sys.stderr)
        print(
            "The vendored responses do not match the shape this generator expects; refusing to "
            "emit a partial or incorrect table. No output files were written.",
            file=sys.stderr,
        )
        return 1

    out_dir = args.out_dir
    if out_dir is None:
        out_dir = Path(__file__).resolve().parent.parent / "src" / "DatabentoDotNet.Reference"
    if not out_dir.is_dir():
        print(f"error: {out_dir} is not a directory", file=sys.stderr)
        return 1

    for filename, content in files.items():
        (out_dir / filename).write_text(content, encoding="utf-8", newline="\n")

    total = sum(n for _, n in counts)
    print(f"Generated {total} members across {len(counts)} types -> {out_dir}")
    for name, count in counts:
        print(f"  {name}.cs ({count})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
