#!/usr/bin/env bash
#
# Everything .github/workflows/publish.yml asks nuget.org before it pushes anything (#103), and the
# push order it derives from the answers.
#
# Usage:  VERSION=0.10.0 PACKAGES="A B" FIRST_PUBLISH="B" tools/publish-preflight.sh
#
# Reads, from the environment:
#   VERSION         the version this run would publish
#   PACKAGES        space-separated package ids this run publishes
#   FIRST_PUBLISH   space-separated subset of PACKAGES whose first *ever* publish this run is
#   FLAT_CONTAINER  base URL of the v3 flat container; the tests point it at a directory
#   GITHUB_OUTPUT   optional. `new` and `order` are appended to it when set
#
# Exits non-zero, having named every problem it found rather than the first, when the run should not
# proceed to the push.
#
# ---------------------------------------------------------------------------------------------
#
# WHY THIS IS A LIST SOMEBODY MAINTAINS RATHER THAN A QUESTION ASKED OF THE REGISTRY.
#
# #102 published 0.10.0 and got a 403 in the middle of the push: nuget.org's Trusted Publishing
# policy was scoped to the four package ids that already existed, and the fifth was new. `dotnet
# nuget push` stops at the first hard failure, so one package was live on the feed declaring a
# dependency on a sibling that did not exist yet, and three were never attempted.
#
# The obvious fix is to ask nuget.org whether the credential may push each id before sending
# anything. That endpoint exists and cannot answer:
#
#     GET https://www.nuget.org/api/v2/verifykey/{id}/{version}
#
# It is a GET, it publishes nothing, and it returns exactly the string the 403 carried — but its
# first act is FindPackageByIdAndVersion, and a null result is a 404 returned *before* any scope is
# evaluated (NuGetGallery, ApiController.VerifyPackageKeyInternalAsync). So it answers only for an
# (id, version) already on the feed, which is the set we never needed to ask about. For a package id
# that does not exist yet there is no read-only endpoint on nuget.org that evaluates "may this
# credential create it". The only thing that answers is the push, and the push is the side effect.
#
# So the hazard is handled rather than interrogated, in two halves that fail independently:
#
#   1. THIS FILE. An id in PACKAGES that is on the feed at no version at all is a first publish, and
#      a first publish needs the Trusted Publishing policy widened before the run, by hand, in a
#      browser. That cannot be verified from here — but it can be *declared*, and an undeclared
#      first publish stops the run with the reason and the policy URL in the message. It converts
#      the 403 from something diagnosed after the fact into a checklist item ahead of the tag.
#
#   2. THE PUSH ORDER, below. A declaration is a human asserting something, so it can be wrong —
#      widened for the wrong account, saved for the wrong pattern, not saved at all. `order` puts
#      the first-publish ids first, and publish.yml pushes one package per invocation, so the id
#      that might 403 is the one attempted while nothing has been published yet. In #102's run that
#      turns "Live is live and its dependency does not exist" into "nothing shipped, fix the policy,
#      re-run" — which --skip-duplicate and the no-op check below already make safe.
#
# Neither half is the check the issue went looking for. Together they bound the failure to a run
# that publishes nothing, which is the part that actually cost something.

set -euo pipefail

version="${VERSION:?VERSION is required}"
packages="${PACKAGES:?PACKAGES is required}"
first_publish="${FIRST_PUBLISH:-}"
flat="${FLAT_CONTAINER:-https://api.nuget.org/v3-flatcontainer}"

policy_url="https://www.nuget.org/account/trustedpublishing"
failures=0

fail() {
  failures=$((failures + 1))
  echo "::error::$1"
}

# Membership in a space-separated list. The surrounding spaces are what stop DatabentoDotNet.Dbn
# from matching inside DatabentoDotNet.Dbn.Extras.
listed() {
  needle="$1"
  haystack="$2"
  case " $haystack " in
    *" $needle "*) return 0 ;;
    *) return 1 ;;
  esac
}

# A declaration about something this run does not publish has no meaning, and reads as though it
# had been checked. Same argument as the partition step's two directions in publish.yml.
for id in $first_publish; do
  if ! listed "$id" "$packages"; then
    fail "FIRST_PUBLISH names $id, which is not in PACKAGES. It declares that this run publishes $id for the first time, and this run does not publish $id at all. Remove it, or add $id to PACKAGES."
  fi
done

new=""
for id in $packages; do
  lower="$(echo "$id" | tr '[:upper:]' '[:lower:]')"
  index="$(mktemp)"

  if curl -sfL "$flat/$lower/index.json" -o "$index"; then
    if jq -e --arg v "$version" '.versions | index($v) != null' "$index" > /dev/null; then
      echo "$id $version is already on nuget.org — it will be skipped."
      state=published
    else
      echo "$id $version is new."
      state=pending
    fi
  else
    echo "$id is not on nuget.org at all — this run would be its first publish."
    state=absent
  fi
  rm -f "$index"

  if [ "$state" = absent ] && ! listed "$id" "$first_publish"; then
    fail "$id has never been published at any version, so this run would create the package id — and nuget.org's Trusted Publishing policy has to already cover it or the push returns 403 partway through, after other packages are live. Widen the policy for jerbersoft at $policy_url so it matches $id, then add $id to FIRST_PUBLISH in publish.yml to record that you did. That is what #102 cost: a policy scoped to the four ids that existed, found by pushing the fifth."
  fi

  if [ "$state" != absent ] && listed "$id" "$first_publish"; then
    fail "FIRST_PUBLISH names $id, but $id is already on nuget.org. That list is what this run claims is new; a stale entry claims a policy was widened for a package id that has not needed one since it was first published. Remove $id from FIRST_PUBLISH."
  fi

  if [ "$state" != published ]; then
    new="$new $id"
  fi
done

new="$(echo "$new" | xargs || true)"

# Refuses a run that would publish nothing.
#
# `dotnet nuget push --skip-duplicate` reports success when a package already exists, and a skipped
# primary push skips its .snupkg too — so a re-run where every version is already on the feed is
# green and has published nothing at all. Run 33280134279 was exactly that. A green tick on this
# workflow has to mean a version reached nuget.org, so the condition is checked before the push
# rather than inferred from its exit code afterwards.
#
# --skip-duplicate stays, because it is what lets a *partial* failure be retried: with four of five
# already up, a re-run should complete the fifth rather than abort on the first conflict. This check
# is what distinguishes that case from a no-op. #102's retry is that path observed rather than
# argued — four Created, one Conflict, run green.
if [ -z "$new" ]; then
  fail "Every package this release publishes already exists at $version. This run would publish nothing — bump VersionPrefix in Directory.Build.props, or delete the release and re-tag."
fi

# First-publish ids first; see half 2 of the argument at the top of this file. Both loops walk
# PACKAGES rather than FIRST_PUBLISH so the order within each half is the one written down.
order=""
for id in $packages; do
  if listed "$id" "$first_publish"; then order="$order $id"; fi
done
for id in $packages; do
  if ! listed "$id" "$first_publish"; then order="$order $id"; fi
done
order="$(echo "$order" | xargs || true)"

if [ "$failures" -ne 0 ]; then
  exit 1
fi

echo "Push order: $order"
echo "New at $version: $new"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  echo "new=$new" >> "$GITHUB_OUTPUT"
  echo "order=$order" >> "$GITHUB_OUTPUT"
fi
