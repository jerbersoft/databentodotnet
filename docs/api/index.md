# API reference

Every public member of the four packages, generated from the XML documentation in the source.

That reference is unusually complete for a first pass, and not through diligence:
`GenerateDocumentationFile` and `TreatWarningsAsErrors` are both on for all four projects, so a
public member without a documentation comment has never compiled in this repository. There is no
undocumented corner to find.

## Namespaces

| Namespace | Package | Contents |
|---|---|---|
| <xref:DatabentoDotNet> | all four | `ApiKey`, `Symbols`, `SymbolsKind`, `UserAgent` — the types every transport shares |
| <xref:DatabentoDotNet.Dbn> | `.Dbn` | Record structs, enums, `DbnDecoder`, `Metadata`, `DbnTime`, symbol maps |
| <xref:DatabentoDotNet.Dbn.Publishers> | `.Dbn` | Generated publisher, dataset and venue tables |
| <xref:DatabentoDotNet.Live> | `.Live` | `LiveClient`, `Subscription`, the gateway and its protocol types |
| <xref:DatabentoDotNet.Historical> | `.Historical` | `HistoricalClient` and its four subclients, and the parameter records they take |
| <xref:DatabentoDotNet.Historical.Json> | `.Historical` | The `JsonConverter<T>` implementations the HTTP payloads are read through |
| <xref:DatabentoDotNet.Reference> | `.Reference` | `ReferenceClient`, security master, corporate actions, adjustment factors, and the generated code tables |
| <xref:DatabentoDotNet.Reference.Json> | `.Reference` | The `JsonConverter<T>` implementations the reference payloads are read through |

The two `.Json` namespaces hold converters, not contexts. Every `JsonSerializerContext` in this
library is `internal sealed partial` and lives in its package's `Internal/` folder, so none of them
appears here. What is public is the 26 `JsonConverter<T>` implementations those contexts register
through `[JsonSourceGenerationOptions]`, and that the reference enums name directly on themselves
with `[JsonConverter]` — `SecurityType` and the nine other code types share one
<xref:DatabentoDotNet.Reference.Json.ReferenceCodeJsonConverter`1> closed over each.

They are documented because they are reachable, not because they are an entry point: nothing in
either namespace is meant to be constructed or called directly. Read them when you want to know
exactly which wire spelling a value round-trips as — each one says, and says what an unrecognised
value does.

## Reading the record structs

The record types in <xref:DatabentoDotNet.Dbn> — <xref:DatabentoDotNet.Dbn.TradeMsg>,
<xref:DatabentoDotNet.Dbn.Mbp1Msg>, <xref:DatabentoDotNet.Dbn.OhlcvMsg> and the rest — document
fields whose types *are* the wire layout. A `ulong` timestamp field is a `ulong` because the wire
carries eight bytes there, and it stays one.

Two things about them that no single member's remarks can state, because both are properties of the
whole library:

- [Zero-Copy and Allocation](../guides/zero-copy-and-allocation.md) —
  a <xref:DatabentoDotNet.Dbn.RecordRef> points into the read buffer and is valid until the next
  call on the decoder. Violating that reads stale bytes rather than throwing.
- [Timestamps and Prices](../guides/timestamps-and-prices.md) —
  why nothing here takes a `DateTime`, and the three sentinels that survive a naive conversion.
