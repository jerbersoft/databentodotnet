#!/usr/bin/env bash
#
# Publishes DatabentoDotNet.AotProbe as a Native AOT binary and runs it (#64).
#
# Two claims, not one. "The AOT publish succeeds" and "the binary runs and reports the right
# numbers" fail independently — the first at ILC, the second at run time in code no analyzer looked
# at — so this script makes both and neither passes for the other.
#
# Usage:  tools/aot-probe.sh [runtime-identifier]
# The RID defaults to the host's. Requires a native toolchain: clang and the platform linker on
# macOS and Linux, the MSVC build tools on Windows.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo/tools/DatabentoDotNet.AotProbe/DatabentoDotNet.AotProbe.csproj"

rid="${1:-$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)}"
if [ -z "$rid" ]; then
  echo "aot-probe: could not determine a runtime identifier; pass one as the first argument." >&2
  exit 2
fi

out="$repo/tools/DatabentoDotNet.AotProbe/bin/Release/net10.0/$rid/publish"
binary="$out/DatabentoDotNet.AotProbe"
[ "${rid#win-}" != "$rid" ] && binary="$binary.exe"

# Deleted rather than overwritten, and this is load-bearing. A failed ILC run leaves the *previous*
# binary in place, so a script that published and then ran whatever was at that path would report a
# clean pass for a publish that had just failed. Observed, not hypothesised.
rm -rf "$out"

echo "==> publishing $rid"
dotnet publish "$project" -c Release -r "$rid" --nologo

if [ ! -f "$binary" ]; then
  echo "aot-probe: no binary at $binary — the publish did not produce one." >&2
  exit 1
fi

# What came out has to be a native image rather than a managed assembly. Nothing inside the process
# can establish this: PublishAot writes the IsDynamicCodeSupported=false feature switch into
# runtimeconfig.json for the ordinary `dotnet build` output too, so a JIT run of this project
# reports itself as having no dynamic code. See the comment in Program.cs.
kind="$(file -b "$binary")"
case "$kind" in
  *Mach-O*executable*|*ELF*executable*|*ELF*shared\ object*|*PE32+*)
    ;;
  *)
    echo "aot-probe: $binary is not a native executable — file(1) says: $kind" >&2
    exit 1
    ;;
esac

echo "==> $(basename "$binary"): $kind, $(wc -c < "$binary" | tr -d ' ') bytes"
echo "==> running"
"$binary"
