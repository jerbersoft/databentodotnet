# Security policy

## Reporting a vulnerability

**[Report it privately through GitHub.](https://github.com/jerbersoft/databentodotnet/security/advisories/new)**
That opens a draft advisory visible only to you and the maintainer, and it is the right channel even
if you are not sure the thing you found is a vulnerability.

**Please do not open a public issue for a suspected vulnerability.** A public issue discloses the
flaw to everyone who consumes these packages before there is a version they can move to. If you have
already opened one, that is not a disaster — say so in a private report and it will be handled from
there.

There is no email address here on purpose. The private channel above is enabled, integrated with the
advisory and CVE workflow, and does not require publishing anyone's inbox on a public repository.

### What to expect

This is a single-maintainer project, so the honest answer is best effort rather than a service level:
an acknowledgement within about a week, and an assessment once the report has been reproduced. If a
report is confirmed, the fix, the advisory and the released version are published together — an
advisory naming a flaw with no version to upgrade to helps nobody.

You will be credited in the advisory unless you would rather not be. Say which.

## Supported versions

| Version | Supported |
|---|---|
| Latest release | ✅ |
| Anything older | ❌ |

Pre-1.0 and single-maintainer, so there are no maintenance branches and no backports. Fixes go into
the next release from `master`. `0.1.0-alpha` in particular was a pipeline test rather than something
to build against, and should not be in use anywhere.

## What is in scope

This is a client library. It holds a credential, opens connections, and parses bytes it did not
produce — which is where its real surface is:

- **Parsing untrusted DBN.** The decoder reinterprets records **in place over the read buffer**
  rather than copying them out, so a length, bounds or alignment mistake is a memory-safety question
  and not merely a wrong answer. A malformed `.dbn`, `.dbn.zst` or `.dbn.frag` that reads outside its
  record, crashes the process, or loops forever is in scope, and this is the most valuable thing to
  look at.
- **Zstandard decompression** of attacker-influenced input, including decompression ratios that a
  caller cannot bound.
- **Credential handling.** The API key must not reach a log line, an exception message, a URL, a
  process argument list, or `ToString()`. `ApiKey` redacts to its bucket id deliberately; a path that
  defeats that is in scope.
- **Transport.** Certificate validation, the live gateway's authentication exchange, and anything
  that would let a network attacker read or alter a session.
- **The published packages themselves** — a package that carries something the repository does not,
  or that fails to match its source.

## What is not in scope

- **Databento's service and API.** Report those to Databento; this repository has no control over
  them and no ability to fix them.
- **Vulnerabilities in dependencies** — NodaTime, `ZstdSharp.Port`,
  `Microsoft.Extensions.Logging.Abstractions`. Report those upstream, but do tell us, because we
  decide what version to depend on.
- **A caller's own key management.** The samples read `DATABENTO_API_KEY` from the environment and
  nothing else; what a consuming application does with its credential is that application's
  responsibility.
- **Cost.** A request that spends more than you expected is a billing matter, not a vulnerability.
  `Metadata.GetCostAsync` prices a request before it runs, and the samples say what they cost before
  they spend anything.

## Hardening already in place

Not a guarantee, but useful context for anyone looking:

- The record structs' sizes are asserted against the `static_assert` values in `databento-cpp`, so a
  layout mistake fails the build rather than silently reading the wrong bytes.
- The vendored corpus — 71 DBN files from `databento/dbn` — is decoded on every test run and must
  produce the record counts upstream reports.
- Secret scanning and push protection are on, and `.env` is git-ignored.
- Builds are deterministic, symbols are published to `symbols.nuget.org`, and SourceLink resolves
  every package back to the commit it was built from.
