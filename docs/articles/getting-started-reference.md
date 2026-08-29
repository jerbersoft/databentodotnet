# Getting started: `DatabentoDotNet.Reference`

Security master, corporate actions, and adjustment factors — the descriptive data that sits beside
market data rather than in it.

```sh
dotnet add package DatabentoDotNet.Reference
```

Brings in `DatabentoDotNet.Historical` (whose HTTP transport it uses) and, through it,
`DatabentoDotNet.Dbn`.

> [!NOTE]
> **Reference data is a separate Databento product.** A key that works for historical or live data
> does not necessarily have reference-data entitlement, and the symptom is a `403` rather than an
> obviously entitlement-shaped error.

## The client and its three subclients

```csharp
using DatabentoDotNet;
using DatabentoDotNet.Reference;

await using var client = new ReferenceClient
{
    ApiKey = new ApiKey(Environment.GetEnvironmentVariable("DATABENTO_API_KEY")!),
};
```

| Subclient | Methods |
|---|---|
| `client.SecurityMaster` | `GetRangeAsync`, `GetLastAsync` |
| `client.CorporateActions` | `GetRangeAsync`, `ListEventsAsync`, `ListEnumsAsync` |
| `client.AdjustmentFactors` | `GetRangeAsync` |

If you already have a `HistoricalClient`, hand it over instead of building a second transport:

```csharp
await using var reference = new ReferenceClient(historicalClient);
```

The reference API *is* the historical transport with a different set of slugs — same host, same
Basic credential, same user agent — which is why this constructor exists rather than a second
connection pool.

## Everything streams

All four `GetRange`-shaped methods return `IAsyncEnumerable<T>`, not a list:

```csharp
using NodaTime;

var range = ReferenceDateTimeRange.Between(
    Instant.FromUtc(2024, 1, 1, 0, 0), Instant.FromUtc(2024, 2, 1, 0, 0));

await foreach (var action in client.CorporateActions.GetRangeAsync(new CorporateActionsGetRangeParams
{
    Symbols = Symbols.From(["AAPL"]),
    StypeIn = SType.RawSymbol,
    DateTimeRange = range,
    AllocateIsins = false,          // read the next section before changing this
}))
{
    Console.WriteLine($"{action.Event}  {action.Symbol}");
}
```

The wire format is zstd-framed JSON Lines — framed in the HTTP body itself rather than announced in
`Content-Encoding`, so `HttpClient` cannot decompress it and the client does. Rows are yielded as
they decode, so a range covering thousands of securities does not have to be materialised before
you can look at the first one.

**Rows arrive in the server's order and are not re-sorted.** Upstream's client sorts client-side;
this one deliberately does not, because a stream cannot be sorted without buffering all of it,
which would give up the property the previous paragraph describes.

Two things about that range are worth knowing precisely, because one of them is not settled.
`ReferenceDateTimeRange.StartingAt(start)` omits the `end` parameter entirely and runs to the end of
the data. And **the `end` of a two-sided range is documented exclusive on upstream's word, not on a
probe** — upstream's own doc comments say "the exclusive end time of the request range", nothing has
confirmed it against the live API, and an attempt to confirm it did not produce an answer. The type
says so in its own remarks rather than presenting the claim as established. If a boundary row
matters to you, treat the edge as unverified.

## `AllocateIsins` — a billing consequence hiding in a default

**`AllocateIsins` defaults to `true`** on all three `GetRange` parameter types, matching upstream's
builder. On an **ISIN-limited plan**, a request for symbols your plan has not seen before is
exactly the request that can create new ISIN allocations against your entitlement — and
`security_master.get_range` is the endpoint whose entire purpose is to return identifiers, so it is
where this bites hardest.

Setting it `false` makes the API drop the rows that would have allocated, rather than returning
them: fewer rows, no allocation.

The default is upstream's and is kept on purpose — a client that silently returned fewer rows than
every other Databento client for the same parameters would be the worse surprise. But it is a
default worth making a deliberate decision about rather than inheriting, especially in anything
that runs unattended.

## Codes are open, not closed enums

Databento's dictionaries move. `Country`, `Currency`, `Event`, `SecurityType` and the rest are
generated tables — `Country` alone is 248 members — and a probe against the real API found several
of them stale in *both* directions: codes the server had that the tables did not, and codes in the
tables the server no longer used.

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
carried through as its raw string with `IsKnown` false. That means a dictionary update on
Databento's side degrades to an unfamiliar code rather than to a deserialization failure in the
middle of a batch.

`ListEnumsAsync` asks the server what it currently believes, which is the way to check a table
against the source rather than against a fixture:

```csharp
IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>> enums =
    await client.CorporateActions.ListEnumsAsync();
```

`ListEventsAsync` is the companion: the server's own documentation of each event type and the
fields it carries. Both are plain `GET`-shaped documentation calls.

## Which calls cost money

`ListEventsAsync` and `ListEnumsAsync` are documentation endpoints and are free. The three
`GetRangeAsync` methods and `GetLastAsync` move data and are billable.

This split is the same one the historical package has, and this repository's tests are organised
around it: the free calls live in one file and the billable ones in another, behind a second
environment-variable gate, so the claim "this test class spends nothing" stays checkable by reading
the file list. See [testing conventions](testing.md).

## Errors

Failures arrive as <xref:DatabentoDotNet.Historical.DatabentoApiException> — the same type the
historical package raises, since it is the same transport. `StatusCode`, `Case`, `DocsUrl` and
`RequestId` are all on it. A `403` here most likely means the key is fine and the *entitlement* is
missing.

## Where to go next

- [Time](time.md) — `ReferenceDateTimeRange` takes `Instant`s rather than `DateTime`s.
- <xref:DatabentoDotNet.Reference.ReferenceClient>,
  <xref:DatabentoDotNet.Reference.SecurityMaster>,
  <xref:DatabentoDotNet.Reference.CorporateAction> in the API reference.
