#!/usr/bin/env python3
"""Generates the DatabentoDotNet.Dbn.Publishers Venue/Dataset/Publisher enums, their DBN wire
string conversions, and the Publisher <-> (Dataset, Venue) cross-mapping, by scraping the `dbn`
Rust crate's `publishers.rs` directly.

Why a scraper and not a hand-port: `publishers.rs` has no upstream generator, build.rs codegen
step, or source data file (JSON/YAML/CSV) in the `dbn` checkout -- it is committed, hand-shaped
Rust source, ~1900 lines, listing 71 + 52 + 145 = 268 enum variants across four parallel,
must-stay-in-sync tables (enum body, `as_str`, `FromStr`, and the Publisher <-> Venue/Dataset
cross-reference methods). Hand-transcribing that into C# would, with near certainty, introduce
an error no test would catch. Scraping it mechanically instead means every future `dbn` release
bump is a one-command regeneration rather than a hand-edit.

Usage:
    python3 tools/generate-publishers.py <path-to-publishers.rs> [--crate-version X.Y.Z]
                                          [--out-dir DIR]

The path to `publishers.rs` is always a required argument -- this script never hard-codes an
absolute path to any checkout of the `dbn` crate.

Failure mode: every parsing and cross-validation step below is a hard assertion. If upstream
changes `publishers.rs`'s shape in a way this script does not recognize -- a new attribute form,
a reordered table, a variant count that no longer matches what was parsed -- the script prints a
diagnostic to stderr and exits non-zero *without* writing any output files. It never emits a
partial or best-effort table; a truncated or silently-wrong generation is treated as strictly
worse than no generation at all.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Tuple
import re

DEFAULT_CRATE_VERSION = "0.68.0"
EXPECTED_COUNTS = {"Venue": 71, "Dataset": 52, "Publisher": 145}

GENERATOR_RELATIVE_PATH = "tools/generate-publishers.py"


class GeneratorError(RuntimeError):
    """Raised when publishers.rs does not match the shape this script knows how to parse."""


def fail(message: str) -> None:
    raise GeneratorError(message)


# --------------------------------------------------------------------------------------- Parsing


@dataclass
class Variant:
    name: str
    value: int
    doc: List[str]
    deprecated: bool = False


@dataclass
class EnumData:
    csharp_name: str
    type_doc: List[str]
    variants: List[Variant]
    as_str: Dict[str, str] = field(default_factory=dict)
    from_str_names: List[str] = field(default_factory=list)  # names in FromStr source order


@dataclass
class PublisherData(EnumData):
    venue_of: Dict[str, str] = field(default_factory=dict)
    dataset_of: Dict[str, str] = field(default_factory=dict)
    from_dataset_venue: Dict[Tuple[str, str], str] = field(default_factory=dict)


def slice_between(text: str, start_marker: str, end_marker: str, search_from: int, label: str) -> Tuple[str, int]:
    start = text.find(start_marker, search_from)
    if start == -1:
        fail(f"{label}: could not find start marker {start_marker!r} (searching from offset {search_from})")
    end = text.find(end_marker, start + len(start_marker))
    if end == -1:
        fail(f"{label}: could not find end marker {end_marker!r} after start marker {start_marker!r}")
    return text[start:end], end


DOC_LINE_RE = re.compile(r"^\s*///\s?(.*)$")
DEPRECATED_ATTR_RE = re.compile(r"^\s*#\[deprecated\b")
ALLOW_ATTR_RE = re.compile(r"^\s*#\[allow\(")
VARIANT_RE = re.compile(r"^\s*(\w+)\s*=\s*(\d+),\s*$")


def parse_enum_body(block: str, rust_enum_name: str) -> List[Variant]:
    """Parses `pub enum {rust_enum_name} { /// doc \\n Variant = N, ... }` into an ordered list
    of variants. Hard-fails on any line inside the body it does not recognize, rather than
    silently skipping it."""
    lines = block.splitlines()
    variants: List[Variant] = []
    pending_doc: List[str] = []
    pending_deprecated = False
    started = False
    header = f"pub enum {rust_enum_name} {{"

    for line in lines:
        if not started:
            if line.strip() == header:
                started = True
            continue

        stripped = line.strip()
        if stripped == "":
            continue
        if stripped == "}":
            break

        m = DOC_LINE_RE.match(line)
        if m:
            pending_doc.append(m.group(1))
            continue

        if DEPRECATED_ATTR_RE.match(line):
            pending_deprecated = True
            continue

        m = VARIANT_RE.match(line)
        if m:
            name, value = m.group(1), int(m.group(2))
            if not pending_doc:
                fail(f"{rust_enum_name}: variant {name!r} has no preceding doc comment")
            variants.append(Variant(name=name, value=value, doc=pending_doc, deprecated=pending_deprecated))
            pending_doc = []
            pending_deprecated = False
            continue

        fail(f"{rust_enum_name}: unrecognized line inside enum body: {line!r}")

    if not started:
        fail(f"{rust_enum_name}: never found enum header {header!r} in the sliced block")
    if not variants:
        fail(f"{rust_enum_name}: parsed zero variants from enum body")
    return variants


def validate_body(variants: List[Variant], rust_enum_name: str, expected_count: int) -> None:
    if len(variants) != expected_count:
        fail(
            f"{rust_enum_name}: expected {expected_count} variants (per upstream's "
            f"{rust_enum_name.upper()}_COUNT / the task brief's recorded count), parsed "
            f"{len(variants)} -- refusing to emit a truncated or padded table"
        )
    names = [v.name for v in variants]
    if len(set(names)) != len(names):
        fail(f"{rust_enum_name}: duplicate variant names parsed: {names}")
    for position, v in enumerate(variants, start=1):
        if v.value != position:
            fail(
                f"{rust_enum_name}: expected contiguous values 1..{expected_count}, but variant "
                f"{v.name!r} at position {position} has value {v.value} -- Publisher -> "
                f"(Dataset, Venue) must never be derived by arithmetic on this value, and a "
                f"non-contiguous range here means this script's assumptions are stale"
            )


ARROW_STRING_RE = re.compile(r'Self::(\w+)\s*=>\s*"([^"]*)",')


def parse_as_str(block: str, rust_enum_name: str, expected_count: int) -> Dict[str, str]:
    pairs = ARROW_STRING_RE.findall(block)
    if len(pairs) != expected_count:
        fail(
            f"{rust_enum_name}: expected {expected_count} as_str() match arms, parsed "
            f"{len(pairs)}"
        )
    mapping: Dict[str, str] = {}
    for name, wire in pairs:
        if name in mapping:
            fail(f"{rust_enum_name}: duplicate as_str() arm for {name!r}")
        mapping[name] = wire
    return mapping


FROM_STR_RE = re.compile(r'"([^"]*)"\s*=>\s*Ok\(Self::(\w+)\),')


def parse_from_str(block: str, rust_enum_name: str, expected_count: int) -> List[Tuple[str, str]]:
    """Returns a list of (wire, name) pairs in source order."""
    pairs = FROM_STR_RE.findall(block)
    if len(pairs) != expected_count:
        fail(
            f"{rust_enum_name}: expected {expected_count} FromStr match arms, parsed "
            f"{len(pairs)}"
        )
    return pairs


VENUE_ARM_RE = re.compile(r"Self::(\w+)\s*=>\s*Venue::(\w+),")
DATASET_ARM_RE = re.compile(r"Self::(\w+)\s*=>\s*Dataset::(\w+),")
FROM_DV_RE = re.compile(r"\(Dataset::(\w+),\s*Venue::(\w+)\)\s*=>\s*Ok\(Self::(\w+)\),")


def extract_type_doc(text: str, enum_start_index: int, window: int = 800) -> List[str]:
    """The enum's own doc comment is the contiguous run of `///` lines immediately above the
    `#[derive(...)]` attribute that precedes `pub enum {Name} {` (in turn immediately above
    `#[non_exhaustive]`/`#[repr(u16)]`/`pub enum ... {`)."""
    chunk = text[max(0, enum_start_index - window):enum_start_index]
    derive_idx = chunk.rfind("#[derive(")
    if derive_idx == -1:
        fail(f"could not find '#[derive(' attribute before offset {enum_start_index}")

    doc_lines_reversed: List[str] = []
    for line in reversed(chunk[:derive_idx].splitlines()):
        if line.strip() == "" and not doc_lines_reversed:
            continue  # tolerate a single blank line directly above #[derive(
        m = DOC_LINE_RE.match(line)
        if not m:
            break
        doc_lines_reversed.append(m.group(1))

    if not doc_lines_reversed:
        fail(f"could not find a type-level doc comment immediately before '#[derive(' at offset {enum_start_index}")
    return list(reversed(doc_lines_reversed))


def parse_publishers_rs(text: str) -> Dict[str, EnumData]:
    cursor = 0

    # ---- Venue --------------------------------------------------------------------------
    venue_start = text.find("pub enum Venue {", cursor)
    if venue_start == -1:
        fail("could not find 'pub enum Venue {' anywhere in publishers.rs")
    venue_type_doc = extract_type_doc(text, venue_start)

    venue_body_block, cursor = slice_between(text, "pub enum Venue {", "pub const VENUE_COUNT", cursor, "Venue body")
    venue_variants = parse_enum_body(venue_body_block, "Venue")
    validate_body(venue_variants, "Venue", EXPECTED_COUNTS["Venue"])

    venue_as_str_block, cursor = slice_between(text, "impl Venue {", "impl AsRef<str> for Venue", cursor, "Venue as_str")
    venue_as_str = parse_as_str(venue_as_str_block, "Venue", EXPECTED_COUNTS["Venue"])

    venue_from_str_block, cursor = slice_between(
        text, "impl std::str::FromStr for Venue {", "/// A source of data.", cursor, "Venue FromStr"
    )
    venue_from_str_pairs = parse_from_str(venue_from_str_block, "Venue", EXPECTED_COUNTS["Venue"])

    venue = EnumData(csharp_name="Venue", type_doc=venue_type_doc, variants=venue_variants, as_str=venue_as_str)
    venue.from_str_names = [name for _, name in venue_from_str_pairs]
    cross_check_as_str_and_from_str(venue, venue_from_str_pairs)

    # ---- Dataset --------------------------------------------------------------------------
    dataset_start = text.find("pub enum Dataset {", cursor)
    if dataset_start == -1:
        fail("could not find 'pub enum Dataset {' anywhere in publishers.rs")
    dataset_type_doc = extract_type_doc(text, dataset_start)

    dataset_body_block, cursor = slice_between(text, "pub enum Dataset {", "pub const DATASET_COUNT", cursor, "Dataset body")
    dataset_variants = parse_enum_body(dataset_body_block, "Dataset")
    validate_body(dataset_variants, "Dataset", EXPECTED_COUNTS["Dataset"])

    # This slice also contains Dataset::publishers()'s body (it comes right after as_str() in
    # the same impl block), but that method's match arms are shaped `Self::X => &[Publisher::Y,
    # ...]`, which never matches the `Self::X => "WIRE",` pattern below -- so it is silently and
    # safely ignored rather than needing its own marker.
    dataset_impl_block, cursor = slice_between(
        text, "impl Dataset {", "impl AsRef<str> for Dataset", cursor, "Dataset as_str/publishers"
    )
    dataset_as_str = parse_as_str(dataset_impl_block, "Dataset", EXPECTED_COUNTS["Dataset"])

    dataset_from_str_block, cursor = slice_between(
        text,
        "impl std::str::FromStr for Dataset {",
        "/// A specific Venue from a specific data source.",
        cursor,
        "Dataset FromStr",
    )
    dataset_from_str_pairs = parse_from_str(dataset_from_str_block, "Dataset", EXPECTED_COUNTS["Dataset"])

    dataset = EnumData(csharp_name="Dataset", type_doc=dataset_type_doc, variants=dataset_variants, as_str=dataset_as_str)
    dataset.from_str_names = [name for _, name in dataset_from_str_pairs]
    cross_check_as_str_and_from_str(dataset, dataset_from_str_pairs)

    # ---- Publisher --------------------------------------------------------------------------
    publisher_start = text.find("pub enum Publisher {", cursor)
    if publisher_start == -1:
        fail("could not find 'pub enum Publisher {' anywhere in publishers.rs")
    publisher_type_doc = extract_type_doc(text, publisher_start)

    publisher_body_block, cursor = slice_between(
        text, "pub enum Publisher {", "pub const PUBLISHER_COUNT", cursor, "Publisher body"
    )
    publisher_variants = parse_enum_body(publisher_body_block, "Publisher")
    validate_body(publisher_variants, "Publisher", EXPECTED_COUNTS["Publisher"])

    # impl Publisher { as_str() venue() dataset() from_dataset_venue() } -- one slice, four
    # independent regexes, since each arm shape (`=> "..."`, `=> Venue::..`, `=> Dataset::..`,
    # `(Dataset::.., Venue::..) => Ok(..)`) is syntactically distinct and cannot cross-match.
    publisher_impl_block, cursor = slice_between(
        text, "impl Publisher {", "impl AsRef<str> for Publisher", cursor, "Publisher impl block"
    )
    publisher_as_str = parse_as_str(publisher_impl_block, "Publisher", EXPECTED_COUNTS["Publisher"])

    venue_pairs = VENUE_ARM_RE.findall(publisher_impl_block)
    if len(venue_pairs) != EXPECTED_COUNTS["Publisher"]:
        fail(f"Publisher: expected {EXPECTED_COUNTS['Publisher']} venue() arms, parsed {len(venue_pairs)}")
    venue_of: Dict[str, str] = {}
    for name, ven in venue_pairs:
        if name in venue_of:
            fail(f"Publisher: duplicate venue() arm for {name!r}")
        venue_of[name] = ven

    dataset_pairs = DATASET_ARM_RE.findall(publisher_impl_block)
    if len(dataset_pairs) != EXPECTED_COUNTS["Publisher"]:
        fail(f"Publisher: expected {EXPECTED_COUNTS['Publisher']} dataset() arms, parsed {len(dataset_pairs)}")
    dataset_of: Dict[str, str] = {}
    for name, ds in dataset_pairs:
        if name in dataset_of:
            fail(f"Publisher: duplicate dataset() arm for {name!r}")
        dataset_of[name] = ds

    from_dv_triples = FROM_DV_RE.findall(publisher_impl_block)
    if len(from_dv_triples) != EXPECTED_COUNTS["Publisher"]:
        fail(
            f"Publisher: expected {EXPECTED_COUNTS['Publisher']} from_dataset_venue() match "
            f"arms, parsed {len(from_dv_triples)}"
        )
    from_dataset_venue: Dict[Tuple[str, str], str] = {}
    seen_publishers_in_from_dv = set()
    for ds, ven, name in from_dv_triples:
        key = (ds, ven)
        if key in from_dataset_venue:
            fail(f"Publisher: duplicate from_dataset_venue() arm for {key}")
        if name in seen_publishers_in_from_dv:
            fail(f"Publisher: {name!r} appears more than once in from_dataset_venue()")
        from_dataset_venue[key] = name
        seen_publishers_in_from_dv.add(name)

    publisher_from_str_block, cursor = slice_between(
        text, "impl std::str::FromStr for Publisher {", '#[cfg(feature = "serde")]', cursor, "Publisher FromStr"
    )
    publisher_from_str_pairs = parse_from_str(publisher_from_str_block, "Publisher", EXPECTED_COUNTS["Publisher"])

    publisher = PublisherData(
        csharp_name="Publisher",
        type_doc=publisher_type_doc,
        variants=publisher_variants,
        as_str=publisher_as_str,
        venue_of=venue_of,
        dataset_of=dataset_of,
        from_dataset_venue=from_dataset_venue,
    )
    publisher.from_str_names = [name for _, name in publisher_from_str_pairs]
    cross_check_as_str_and_from_str(publisher, publisher_from_str_pairs)

    # ---- Cross-enum consistency ------------------------------------------------------------
    publisher_names = {v.name for v in publisher_variants}
    venue_names = {v.name for v in venue_variants}
    dataset_names = {v.name for v in dataset_variants}

    if set(venue_of.keys()) != publisher_names:
        fail("Publisher: venue() arm names do not exactly match the enum body's variant names")
    if set(dataset_of.keys()) != publisher_names:
        fail("Publisher: dataset() arm names do not exactly match the enum body's variant names")
    for name in publisher_names:
        if venue_of[name] not in venue_names:
            fail(f"Publisher::{name}: venue() targets undefined Venue variant {venue_of[name]!r}")
        if dataset_of[name] not in dataset_names:
            fail(f"Publisher::{name}: dataset() targets undefined Dataset variant {dataset_of[name]!r}")

    # from_dataset_venue() must be *exactly* the inverse of the venue()/dataset() maps: same
    # 145 (dataset, venue) -> publisher pairs, reachable both ways.
    derived_from_dv = {(dataset_of[name], venue_of[name]): name for name in publisher_names}
    if derived_from_dv != from_dataset_venue:
        only_in_derived = set(derived_from_dv.items()) - set(from_dataset_venue.items())
        only_in_parsed = set(from_dataset_venue.items()) - set(derived_from_dv.items())
        fail(
            "Publisher: from_dataset_venue() does not agree with venue()+dataset() as an exact "
            f"inverse. Only via venue()/dataset(): {sorted(only_in_derived)[:5]}. Only in "
            f"from_dataset_venue(): {sorted(only_in_parsed)[:5]}."
        )

    return {"Venue": venue, "Dataset": dataset, "Publisher": publisher}


def cross_check_as_str_and_from_str(enum_data: EnumData, from_str_pairs: List[Tuple[str, str]]) -> None:
    body_names = {v.name for v in enum_data.variants}
    as_str_names = set(enum_data.as_str.keys())
    from_str_names = set(name for _, name in from_str_pairs)

    if as_str_names != body_names:
        fail(f"{enum_data.csharp_name}: as_str() variant names do not match the enum body's variant names")
    if from_str_names != body_names:
        fail(f"{enum_data.csharp_name}: FromStr variant names do not match the enum body's variant names")

    for wire, name in from_str_pairs:
        if enum_data.as_str.get(name) != wire:
            fail(
                f"{enum_data.csharp_name}: FromStr maps {wire!r} -> {name!r}, but as_str() maps "
                f"{name!r} -> {enum_data.as_str.get(name)!r} -- as_str/FromStr are not exact "
                f"inverses"
            )


# ------------------------------------------------------------------------------------- Emission


def xml_escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def render_summary(doc_lines: List[str], indent: str) -> List[str]:
    escaped = [xml_escape(line) for line in doc_lines]
    if len(escaped) == 1:
        return [f"{indent}/// <summary>{escaped[0]}</summary>"]
    out = [f"{indent}/// <summary>"]
    out.extend(f"{indent}/// {line}" for line in escaped)
    out.append(f"{indent}/// </summary>")
    return out


def auto_generated_header(upstream_file: str, crate_version: str, extra: Optional[str] = None) -> List[str]:
    lines = [
        "// <auto-generated>",
        f"//   Generated by {GENERATOR_RELATIVE_PATH} from the `dbn` crate's {upstream_file}",
        f"//   (crate version {crate_version}). Do not hand-edit.",
        "//   Regenerate with: python3 tools/generate-publishers.py <path-to-publishers.rs>",
    ]
    if extra:
        lines.append(f"//   {extra}")
    lines.append("// </auto-generated>")
    lines.append("")
    # Roslyn recognizes the "<auto-generated>" comment above and treats this file as generated
    # code, which disables the project-wide `<Nullable>enable</Nullable>` annotation context for
    # it (CS8669) unless restated explicitly here.
    lines.append("#nullable enable")
    lines.append("")
    return lines


def render_enum_file(
    enum_data: EnumData,
    upstream_file: str,
    crate_version: str,
    remarks_extra: str,
) -> str:
    lines: List[str] = []
    lines.extend(auto_generated_header(upstream_file, crate_version))
    lines.append("namespace DatabentoDotNet.Dbn.Publishers;")
    lines.append("")
    lines.extend(render_summary(enum_data.type_doc, ""))
    lines.append("/// <remarks>")
    lines.append(
        f"/// Mechanically derived from the <c>dbn</c> crate's <c>{upstream_file}</c> "
        f"(v{crate_version}) by <c>{GENERATOR_RELATIVE_PATH}</c>. {len(enum_data.variants)} "
        f"variants, contiguous <c>1..{len(enum_data.variants)}</c>. Upstream marks this type "
        "<c>#[non_exhaustive]</c>; there is no default (zero) variant. See "
        '<see cref="PublisherWireStrings"/> for wire-string conversions'
        f"{remarks_extra}."
    )
    lines.append("/// </remarks>")
    lines.append(f"public enum {enum_data.csharp_name} : ushort")
    lines.append("{")
    for i, v in enumerate(enum_data.variants):
        lines.extend(render_summary(v.doc, "    "))
        lines.append(f"    {v.name} = {v.value},")
        if i != len(enum_data.variants) - 1:
            lines.append("")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def render_wire_strings_file(
    venue: EnumData, dataset: EnumData, publisher: PublisherData, upstream_file: str, crate_version: str
) -> str:
    lines: List[str] = []
    lines.extend(auto_generated_header(upstream_file, crate_version))
    lines.append("namespace DatabentoDotNet.Dbn.Publishers;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append(
        "/// Allocation-free, reflection-free DBN wire string conversions for "
        '<see cref="Venue"/>, <see cref="Dataset"/>, and <see cref="Publisher"/>.'
    )
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append(
        f"/// Mechanically generated from <c>{upstream_file}</c> (v{crate_version}) by "
        f"<c>{GENERATOR_RELATIVE_PATH}</c> -- see that type's own <c>as_str</c>/<c>FromStr</c> "
        "in the Rust source. No aliases: every wire string accepted by a "
        '<c>TryParse{Enum}</c> method here is also the one <c>ToWireString</c> emits for the '
        "matching value. One method per enum rather than a single overload distinguished only "
        "by its <see langword=\"out\"/> parameter's type, matching "
        '<see cref="WireStrings"/>\'s convention -- an overload would '
        "make the ordinary <c>out var</c> call form ambiguous and fail to compile."
    )
    lines.append("/// </remarks>")
    lines.append("public static class PublisherWireStrings")
    lines.append("{")

    for idx, (name, data) in enumerate([("Venue", venue), ("Dataset", dataset), ("Publisher", publisher)]):
        lines.append(f"    // {'-' * 16} {name}")
        lines.append("")
        lines.append(f"    /// <summary>Converts <paramref name=\"value\"/> to its DBN wire string.</summary>")
        lines.append(
            f'    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="{name}"/>.</exception>'
        )
        lines.append(f"    public static string ToWireString(this {name} value) => value switch")
        lines.append("    {")
        for v in data.variants:
            wire = data.as_str[v.name]
            lines.append(f'        {name}.{v.name} => "{wire}",')
        lines.append(
            f'        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined {name}."),'
        )
        lines.append("    };")
        lines.append("")
        lines.append(f'    /// <summary>Tries to parse a DBN wire string into a <see cref="{name}"/>. No aliases.</summary>')
        lines.append(f"    public static bool TryParse{name}(string? value, out {name} result)")
        lines.append("    {")
        lines.append("        switch (value)")
        lines.append("        {")
        for v in data.variants:
            wire = data.as_str[v.name]
            lines.append(f'            case "{wire}": result = {name}.{v.name}; return true;')
        lines.append("            default: result = default; return false;")
        lines.append("        }")
        lines.append("    }")
        if idx != 2:
            lines.append("")

    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def render_values_file(
    venue: EnumData, dataset: EnumData, publisher: PublisherData, upstream_file: str, crate_version: str
) -> str:
    lines: List[str] = []
    lines.extend(auto_generated_header(upstream_file, crate_version))
    lines.append("namespace DatabentoDotNet.Dbn.Publishers;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append(
        "/// Validates a raw wire word against the discriminants "
        '<see cref="Publisher"/>, <see cref="Dataset"/>, and <see cref="Venue"/> actually define.'
    )
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append("/// <para>")
    lines.append(
        "/// What <c>EnumValues</c> is for the enums declared directly in "
        "<c>DatabentoDotNet.Dbn</c>, this is for the three declared here -- the equivalent of "
        "upstream's <c>num_enum</c>-derived <c>TryFrom&lt;u16&gt;</c> impls. They are not "
        "folded into <c>EnumValues</c> because these three tables are generated: keeping the "
        "validator beside the enum it validates means a <c>dbn</c> release bump regenerates "
        "both from one source, where a hand-maintained half in another namespace would have "
        "to be remembered."
    )
    lines.append("/// </para>")
    lines.append("/// <para>")
    lines.append(
        "/// <b>This is the checked conversion for <see cref=\"RecordHeader.PublisherId\"/></b>, "
        "which is a raw <see langword=\"ushort\"/> off the wire and is not validated on "
        "decode -- the same arrangement <see cref=\"RecordHeader.RawRType\"/> has with "
        "<see cref=\"EnumValues.TryFromRType(byte, out RType)\"/>. Casting an unvalidated "
        "word to <see cref=\"Publisher\"/> and then calling "
        "<see cref=\"PublisherMappings.ToVenue(Publisher)\"/> turns an unknown publisher "
        "into an <see cref=\"ArgumentOutOfRangeException\"/> from deep inside a lookup; "
        "going through <see cref=\"TryFromPublisher\"/> first makes it a "
        "<see langword=\"bool\"/> the caller decides about."
    )
    lines.append("/// </para>")
    lines.append("/// <para>")
    lines.append(
        "/// Strict, like every validator in <c>EnumValues</c>: an undefined word is rejected even "
        "though upstream marks all three types <c>#[non_exhaustive]</c>. <c>#[non_exhaustive]</c> "
        "governs whether downstream Rust may exhaustively <c>match</c> the type, not whether an "
        "arbitrary word is a valid instance of it. Rejection here is the numeric out-of-range "
        "failure mode; an unrecognized wire <em>string</em> is a distinct failure handled by "
        '<see cref="PublisherWireStrings"/>.'
    )
    lines.append("/// </para>")
    lines.append("/// <para>")
    lines.append(
        "/// One method per enum rather than a single <c>TryFrom</c> overloaded on its "
        '<see langword="out"/> parameter\'s type: overload resolution needs that type before it '
        "can pick an overload, so <c>out var</c> at a call site -- the ordinary way to call a "
        "<c>TryXxx</c> method -- cannot disambiguate and fails to compile. Same convention as "
        "<c>EnumValues</c> and <see cref=\"PublisherWireStrings\"/>."
    )
    lines.append("/// </para>")
    lines.append("/// </remarks>")
    lines.append("public static class PublisherValues")
    lines.append("{")

    for idx, (name, data) in enumerate([("Publisher", publisher), ("Dataset", dataset), ("Venue", venue)]):
        lines.append(f"    // {'-' * 16} {name}")
        lines.append("")
        lines.append(
            f'    /// <summary>Tries to validate <paramref name="raw"/> as a defined <see cref="{name}"/>.</summary>'
        )
        lines.append(
            f'    /// <param name="raw">The raw wire word, such as <see cref="RecordHeader.PublisherId"/>.</param>'
            if name == "Publisher"
            else f'    /// <param name="raw">The raw wire word.</param>'
        )
        lines.append(
            f'    /// <param name="value">The validated <see cref="{name}"/>, or <see langword="default"/> when <paramref name="raw"/> is not one.</param>'
        )
        lines.append(
            f'    /// <returns><see langword="true"/> when <paramref name="raw"/> is a defined <see cref="{name}"/>.</returns>'
        )
        lines.append(f"    public static bool TryFrom{name}(ushort raw, out {name} value)")
        lines.append("    {")
        lines.append("        switch (raw)")
        lines.append("        {")
        for v in data.variants:
            lines.append(f"            case (ushort){name}.{v.name}: value = {name}.{v.name}; return true;")
        lines.append("            default: value = default; return false;")
        lines.append("        }")
        lines.append("    }")
        if idx != 2:
            lines.append("")

    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def render_mappings_file(publisher: PublisherData, upstream_file: str, crate_version: str) -> str:
    lines: List[str] = []
    lines.extend(auto_generated_header(upstream_file, crate_version))
    lines.append("namespace DatabentoDotNet.Dbn.Publishers;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append(
        '/// Conversions between <see cref="Publisher"/> and its <see cref="Venue"/> / '
        '<see cref="Dataset"/> components.'
    )
    lines.append("/// </summary>")
    lines.append("/// <remarks>")
    lines.append(
        f"/// Mechanically generated from <c>{upstream_file}</c> (v{crate_version}) by "
        f"<c>{GENERATOR_RELATIVE_PATH}</c>, copying upstream's <c>Publisher::venue()</c>, "
        '<c>Publisher::dataset()</c>, and <c>Publisher::from_dataset_venue()</c> match tables '
        "verbatim. <b>Publisher values ascend 1-145 but are not grouped by Dataset</b> -- "
        "venues added to an already-published dataset in a later <c>dbn</c> release are "
        "appended at the end of the enum rather than inserted into their dataset's original "
        "block, so a <see cref=\"Dataset\"/>'s <see cref=\"Publisher\"/> values do not form a "
        "contiguous run. There is no formula relating a publisher id to its dataset or venue "
        "id; every lookup below is table-driven, never derived by arithmetic."
    )
    lines.append("/// </remarks>")
    lines.append("public static class PublisherMappings")
    lines.append("{")
    lines.append('    /// <summary>Returns the <see cref="Venue"/> for <paramref name="value"/>.</summary>')
    lines.append(
        '    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="Publisher"/>.</exception>'
    )
    lines.append("    public static Venue ToVenue(this Publisher value) => value switch")
    lines.append("    {")
    for v in publisher.variants:
        lines.append(f"        Publisher.{v.name} => Venue.{publisher.venue_of[v.name]},")
    lines.append('        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined Publisher."),')
    lines.append("    };")
    lines.append("")
    lines.append('    /// <summary>Returns the <see cref="Dataset"/> for <paramref name="value"/>.</summary>')
    lines.append(
        '    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined <see cref="Publisher"/>.</exception>'
    )
    lines.append("    public static Dataset ToDataset(this Publisher value) => value switch")
    lines.append("    {")
    for v in publisher.variants:
        lines.append(f"        Publisher.{v.name} => Dataset.{publisher.dataset_of[v.name]},")
    lines.append('        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined Publisher."),')
    lines.append("    };")
    lines.append("")
    lines.append(
        '    /// <summary>\n'
        '    /// Tries to find the <see cref="Publisher"/> for the given <paramref name="dataset"/> and\n'
        '    /// <paramref name="venue"/> combination.\n'
        '    /// </summary>\n'
        '    /// <remarks>\n'
        '    /// Most of the 71 &#215; 52 possible (<see cref="Venue"/>, <see cref="Dataset"/>) combinations are not\n'
        '    /// a real publisher and return <see langword="false"/>; only the 145 pairs upstream defines succeed.\n'
        '    /// </remarks>'
    )
    lines.append("    public static bool TryFromDatasetVenue(Dataset dataset, Venue venue, out Publisher publisher)")
    lines.append("    {")
    lines.append("        switch (dataset, venue)")
    lines.append("        {")
    for v in publisher.variants:
        ds = publisher.dataset_of[v.name]
        ven = publisher.venue_of[v.name]
        lines.append(f"            case (Dataset.{ds}, Venue.{ven}): publisher = Publisher.{v.name}; return true;")
    lines.append("            default: publisher = default; return false;")
    lines.append("        }")
    lines.append("    }")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


# ------------------------------------------------------------------------------------------ Main


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("publishers_rs", type=Path, help="Path to the dbn crate's publishers.rs")
    parser.add_argument(
        "--crate-version",
        default=DEFAULT_CRATE_VERSION,
        help=f"dbn crate version to record in the generated header (default: {DEFAULT_CRATE_VERSION})",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=None,
        help="Output directory (default: <repo>/src/DatabentoDotNet.Dbn/Publishers, relative to this script)",
    )
    args = parser.parse_args(argv)

    if not args.publishers_rs.is_file():
        print(f"error: {args.publishers_rs} is not a file", file=sys.stderr)
        return 1

    text = args.publishers_rs.read_text(encoding="utf-8")
    upstream_file = "publishers.rs"

    try:
        data = parse_publishers_rs(text)
    except GeneratorError as exc:
        print(f"error: {exc}", file=sys.stderr)
        print(
            "publishers.rs does not match the shape this generator expects; refusing to emit "
            "a partial or incorrect table. No output files were written.",
            file=sys.stderr,
        )
        return 1

    venue = data["Venue"]
    dataset = data["Dataset"]
    publisher = data["Publisher"]
    assert isinstance(publisher, PublisherData)

    out_dir = args.out_dir
    if out_dir is None:
        repo_root = Path(__file__).resolve().parent.parent
        out_dir = repo_root / "src" / "DatabentoDotNet.Dbn" / "Publishers"
    out_dir.mkdir(parents=True, exist_ok=True)

    files = {
        "Venue.cs": render_enum_file(
            venue,
            upstream_file,
            args.crate_version,
            "",
        ),
        "Dataset.cs": render_enum_file(
            dataset,
            upstream_file,
            args.crate_version,
            ' and <see cref="PublisherMappings"/> for the mapping to and from '
            '<see cref="Publisher"/>. Two variants (<see cref="Dataset.FinnNls"/>, '
            '<see cref="Dataset.FinyTrades"/>) are marked deprecated upstream since dbn 0.17.0 '
            "-- they still round-trip through the wire-string conversions, but no "
            '<see cref="Publisher"/> is associated with either any more',
        ),
        "Publisher.cs": render_enum_file(
            publisher,
            upstream_file,
            args.crate_version,
            ' and <see cref="PublisherMappings"/> for the mapping to and from its '
            '<see cref="Venue"/> and <see cref="Dataset"/>',
        ),
        "PublisherWireStrings.cs": render_wire_strings_file(venue, dataset, publisher, upstream_file, args.crate_version),
        "PublisherValues.cs": render_values_file(venue, dataset, publisher, upstream_file, args.crate_version),
        "PublisherMappings.cs": render_mappings_file(publisher, upstream_file, args.crate_version),
    }

    for filename, content in files.items():
        (out_dir / filename).write_text(content, encoding="utf-8", newline="\n")

    print(
        f"Generated {len(venue.variants)} Venue, {len(dataset.variants)} Dataset, "
        f"{len(publisher.variants)} Publisher variants -> {out_dir}"
    )
    for filename in files:
        print(f"  {out_dir / filename}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
