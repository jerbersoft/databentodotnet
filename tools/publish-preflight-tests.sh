#!/usr/bin/env bash
#
# Exercises tools/publish-preflight.sh against a fake flat container (#103).
#
# Usage:  tools/publish-preflight-tests.sh
#
# There is one reason this file exists rather than a paragraph asserting the gates are right.
# `publish.yml` has now grown four pre-flights, and every one of them was added after a defect found
# it — a hardcoded version and a no-op run reporting success (#71), a push glob covering more
# packages than the gates checked (#102), and a credential that could not create a new package id
# (#103). That is not four unlucky releases. It is what happens when the only way to run a file is
# to publish something: the gates were unrunnable, so they were unexercised, so they were wrong.
#
# So the questions the release asks nuget.org live in a script, and the script has a fake feed. Every
# branch below can be run in under a second, on any machine, with nothing published and no
# credential — including the exact shape of #102's failure, which cost a release to find the first
# time.
#
# The fake feed is a directory of {lowercase-id}/index.json served over file://, which is the flat
# container's whole contract as far as the pre-flight is concerned: an index that exists and lists
# versions, or a URL that does not resolve. curl reports a missing file as a non-zero exit exactly
# as it reports a 404, which is the one property this substitution rests on.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/publish-preflight.sh"

command -v jq > /dev/null || { echo "publish-preflight-tests: jq is required." >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

passed=0
failed=0
out=""
status=0
outputs=""

# Builds a fake flat container. Each argument is `id:version,version`; an id with no versions is
# absent from the feed entirely, which is how a package that has never been published looks.
feed() {
  rm -rf "$work/feed"
  for entry in "$@"; do
    id="${entry%%:*}"
    versions="${entry#*:}"
    [ -z "$versions" ] && continue
    lower="$(echo "$id" | tr '[:upper:]' '[:lower:]')"
    mkdir -p "$work/feed/$lower"
    printf '{"versions":[%s]}\n' "$(echo "$versions" | sed 's/[^,][^,]*/"&"/g')" > "$work/feed/$lower/index.json"
  done
}

run() {
  outputs="$work/outputs"
  : > "$outputs"
  status=0
  out="$(VERSION="$1" PACKAGES="$2" FIRST_PUBLISH="${3:-}" \
         FLAT_CONTAINER="file://$work/feed" GITHUB_OUTPUT="$outputs" \
         bash "$script" 2>&1)" || status=$?
}

check() {
  if [ "$1" = pass ]; then
    passed=$((passed + 1))
  else
    failed=$((failed + 1))
    echo "  FAIL: $2"
    echo "$out" | sed 's/^/        | /'
  fi
}

expect_exit() {
  if [ "$status" -eq "$1" ]; then check pass; else check fail "expected exit $1, got $status"; fi
}

expect_says() {
  case "$out" in
    *"$1"*) check pass ;;
    *) check fail "expected the output to mention: $1" ;;
  esac
}

expect_silent_about() {
  case "$out" in
    *"$1"*) check fail "expected the output NOT to mention: $1" ;;
    *) check pass ;;
  esac
}

expect_output() {
  actual="$(sed -n "s/^$1=//p" "$outputs")"
  if [ "$actual" = "$2" ]; then check pass; else check fail "expected $1='$2', got '$actual'"; fi
}

expect_errors() {
  actual="$(grep -c '^::error::' <<< "$out" || true)"
  if [ "$actual" -eq "$1" ]; then check pass; else check fail "expected $1 errors, got $actual"; fi
}

five="DatabentoDotNet.Dbn DatabentoDotNet.Live DatabentoDotNet.Historical DatabentoDotNet.Reference DatabentoDotNet.Extensions.Hosting"

echo "A release where nothing is new is refused rather than reported green"
feed "DatabentoDotNet.Dbn:0.9.0,0.10.0" "DatabentoDotNet.Live:0.9.0,0.10.0" \
     "DatabentoDotNet.Historical:0.9.0,0.10.0" "DatabentoDotNet.Reference:0.9.0,0.10.0" \
     "DatabentoDotNet.Extensions.Hosting:0.10.0"
run 0.10.0 "$five"
expect_exit 1
expect_says "would publish nothing"

echo "An ordinary release of five known ids passes, in the order PACKAGES names them"
feed "DatabentoDotNet.Dbn:0.10.0" "DatabentoDotNet.Live:0.10.0" "DatabentoDotNet.Historical:0.10.0" \
     "DatabentoDotNet.Reference:0.10.0" "DatabentoDotNet.Extensions.Hosting:0.10.0"
run 0.11.0 "$five"
expect_exit 0
expect_output new "$five"
expect_output order "$five"

echo "#102's release, undeclared: the fifth id is on the feed at no version, and the run stops"
feed "DatabentoDotNet.Dbn:0.9.1" "DatabentoDotNet.Live:0.9.1" "DatabentoDotNet.Historical:0.9.1" \
     "DatabentoDotNet.Reference:0.9.1" "DatabentoDotNet.Extensions.Hosting:"
run 0.10.0 "$five"
expect_exit 1
expect_errors 1
expect_says "DatabentoDotNet.Extensions.Hosting has never been published"
expect_says "Trusted Publishing"
expect_says "https://www.nuget.org/account/trustedpublishing"

echo "#102's release, declared: it passes, and the new id is pushed first"
run 0.10.0 "$five" "DatabentoDotNet.Extensions.Hosting"
expect_exit 0
expect_output order "DatabentoDotNet.Extensions.Hosting DatabentoDotNet.Dbn DatabentoDotNet.Live DatabentoDotNet.Historical DatabentoDotNet.Reference"
expect_output new "$five"

echo "A declaration left behind after the package has shipped is refused"
feed "DatabentoDotNet.Dbn:0.10.0" "DatabentoDotNet.Live:0.10.0" "DatabentoDotNet.Historical:0.10.0" \
     "DatabentoDotNet.Reference:0.10.0" "DatabentoDotNet.Extensions.Hosting:0.10.0"
run 0.11.0 "$five" "DatabentoDotNet.Extensions.Hosting"
expect_exit 1
expect_errors 1
expect_says "already on nuget.org"

echo "A declaration about a package the run does not publish is refused"
run 0.11.0 "DatabentoDotNet.Dbn" "DatabentoDotNet.Live"
expect_exit 1
expect_says "which is not in PACKAGES"

echo "The retry path survives: one id already at the version, the rest still pushed"
feed "DatabentoDotNet.Dbn:0.9.1" "DatabentoDotNet.Live:0.9.1,0.10.0" "DatabentoDotNet.Historical:0.9.1" \
     "DatabentoDotNet.Reference:0.9.1" "DatabentoDotNet.Extensions.Hosting:"
run 0.10.0 "$five" "DatabentoDotNet.Extensions.Hosting"
expect_exit 0
expect_says "DatabentoDotNet.Live 0.10.0 is already on nuget.org"
expect_output new "DatabentoDotNet.Dbn DatabentoDotNet.Historical DatabentoDotNet.Reference DatabentoDotNet.Extensions.Hosting"
expect_output order "DatabentoDotNet.Extensions.Hosting DatabentoDotNet.Dbn DatabentoDotNet.Live DatabentoDotNet.Historical DatabentoDotNet.Reference"

echo "Every problem is named, not just the first"
feed "DatabentoDotNet.Dbn:0.9.1" "DatabentoDotNet.Live:" "DatabentoDotNet.Historical:0.9.1" \
     "DatabentoDotNet.Reference:" "DatabentoDotNet.Extensions.Hosting:"
run 0.10.0 "$five" "DatabentoDotNet.Extensions.Hosting"
expect_exit 1
expect_errors 2
expect_says "DatabentoDotNet.Live has never been published"
expect_says "DatabentoDotNet.Reference has never been published"
expect_silent_about "DatabentoDotNet.Extensions.Hosting has never been published"

echo "An id is not its own prefix: A.Dbn is not covered by a declaration naming A.Dbn.Extras"
feed "A.Dbn:" "A.Dbn.Extras:"
run 1.0.0 "A.Dbn A.Dbn.Extras" "A.Dbn.Extras"
expect_exit 1
expect_errors 1
expect_says "A.Dbn has never been published"

echo
if [ "$failed" -eq 0 ]; then
  echo "publish-preflight: $passed checks passed."
else
  echo "publish-preflight: $failed of $((passed + failed)) checks FAILED."
  exit 1
fi
