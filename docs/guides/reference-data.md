# Reference Data

**Reference data is a separate Databento product, and one default in this package can spend your
entitlement rather than your money.** This page covers the security master, corporate actions and
adjustment factors: what streams, what a code table does when it does not recognise a code, and the
`AllocateIsins` default to make a decision about.

For a first working program, start with [Getting Started](getting-started.md).

---

## The client

```csharp
using DatabentoDotNet;          // ApiKey, Symbols
using DatabentoDotNet.Dbn;      // SType
using DatabentoDotNet.Reference;

await using var client = new ReferenceClient { ApiKey = new ApiKey(apiKeyString) };
```

| Subclient | Methods | Billable |
|---|---|---|
| `client.SecurityMaster` | `GetRangeAsync`, `GetLastAsync` | **Yes** |
| `client.CorporateActions` | `GetRangeAsync` | **Yes** |
| | `ListEventsAsync`, `ListEnumsAsync` | No — documentation endpoints |
| `client.AdjustmentFactors` | `GetRangeAsync` | **Yes** |

If you already have a `HistoricalClient`, hand it over rather than building a second transport:

```csharp
await using var reference = new ReferenceClient(historicalClient);
```

The reference API *is* the historical transport with a different set of slugs — same host, same
Basic credential, same user agent. Upstream says so by construction, and that is why this
constructor exists instead of a second connection pool. See [Historical Data](historical-data.md) for
the transport itself.

> A key that works for live or historical data does not necessarily carry reference-data
> entitlement, and the symptom is a bare `403` rather than anything entitlement-shaped.

## Everything streams

All four range methods return `IAsyncEnumerable<T>`, not a list:

```csharp
using NodaTime;

var range = ReferenceDateTimeRange.Between(
    Instant.FromUtc(2024, 1, 1, 0, 0), Instant.FromUtc(2024, 2, 1, 0, 0));

await foreach (var action in client.CorporateActions.GetRangeAsync(
    new CorporateActionsGetRangeParams
    {
        Symbols       = Symbols.From(["AAPL"]),
        StypeIn       = SType.RawSymbol,
        DateTimeRange = range,
        AllocateIsins = false,          // read the next section first
    },
    ct))
{
    Console.WriteLine($"{action.Event}  {action.Symbol}  {action.EventDate}");
}
```

The wire format is zstd-framed JSON Lines — framed in the HTTP body rather than announced in
`Content-Encoding`, so `HttpClient` cannot decompress it and the client does. Rows are yielded as
they decode, so a range covering thousands of securities does not have to be materialised before you
can look at the first one.

**Rows arrive in the server's order and are not re-sorted.** Upstream's client sorts client-side;
this one deliberately does not, because sorting a stream means buffering all of it, which gives up
the property the previous paragraph describes.

`ReferenceDateTimeRange.StartingAt(start)` omits the `end` parameter entirely and runs to the end of
the data.

> **The exclusive end is documented, not probed.** Upstream's doc comments say "the exclusive end
> time of the request range"; nothing has confirmed it against the live API, and an attempt to
> confirm it did not produce an answer. The type says so in its own remarks rather than presenting
> the claim as settled. If a boundary row matters to you, treat the edge as unverified.

## `AllocateIsins` — the default worth a decision

**`AllocateIsins` defaults to `true`** on all three range parameter types, matching upstream's
builder.

On an **ISIN-limited plan**, a request for symbols your plan has not seen before is exactly the
request that can create new ISIN allocations against your entitlement. `security_master.get_range`
is the endpoint whose entire purpose is to return identifiers, so it is where this bites hardest.

Setting it `false` makes the API drop the rows that would have allocated rather than returning them:
fewer rows, no allocation.

**The default is upstream's and is kept on purpose.** A client that silently returned fewer rows
than every other Databento client for the same parameters would be the worse surprise. But it is a
default to decide about rather than inherit, particularly in anything that runs unattended — and in
this repository it is a rule rather than advice: no test that reaches the real API may leave it
`true` without going through the billable-test gate.

## Codes are open, not closed enums

Databento's dictionaries move. `Country`, `Currency`, `Event`, `SecurityType` and the rest are
generated tables — `Country` alone is 248 members — and a probe against the real API found several
stale in *both* directions: codes the server had that the tables did not, and codes in the tables
the server no longer used.

So these are not C# `enum`s. They are readonly structs with named statics and an unknown carrier:

```csharp
Country country = security.ListingCountry;

if (country.IsKnown)
{
    // One of the generated members.
}
else if (country.HasValue)
{
    // A code the server sent that this version's table does not name.
    Console.WriteLine($"unrecognised country code: {country.Code}");
}
```

A code the tables do not know **does not throw and does not silently become a default** — it is
carried through as its raw string with `IsKnown` false. A dictionary update on Databento's side
therefore degrades to an unfamiliar code rather than to a deserialization failure in the middle of a
batch.

`ListEnumsAsync` asks the server what it currently believes, which is how to check a table against
the source rather than against a fixture:

```csharp
IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>> enums =
    await client.CorporateActions.ListEnumsAsync(ct);
```

`ListEventsAsync` is its companion: the server's own documentation of each event type and the fields
it carries. Both are free.

## Errors

Failures arrive as `DatabentoApiException` — the same type [Historical Data](historical-data.md)
raises, since it is the same transport. `StatusCode`, `Case`, `DocsUrl` and `RequestId` are all on
it. A `403` here most likely means the key is fine and the entitlement is missing.

## See also

- [Getting Started](getting-started.md) — building, the API key, a first program
- [Historical Data](historical-data.md) — the transport this rides on, and its cost table
- [Timestamps and Prices](timestamps-and-prices.md) — `decimal` over `double`, and the sentinels
- [Troubleshooting](troubleshooting.md) — specific error messages and what they mean
- [`ROADMAP.md` §6](https://github.com/jerbersoft/databentodotnet/blob/master/ROADMAP.md) — the design decisions behind this client
