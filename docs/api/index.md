# API reference

Every public member of the four packages, generated from the XML documentation in the source.

That reference is unusually complete for a first pass, and not through diligence:
`GenerateDocumentationFile` and `TreatWarningsAsErrors` are both on for all four projects, so a
public member without a documentation comment has never compiled in this repository. There is no
undocumented corner to find.

## Namespaces

| Namespace | Package | Contents |
|---|---|---|
| <xref:DatabentoDotNet> | all four | `ApiKey`, `Symbols`, `UserAgent` — the types every transport shares |
| <xref:DatabentoDotNet.Dbn> | `.Dbn` | Record structs, enums, `DbnDecoder`, `Metadata`, `DbnTime`, symbol maps |
| <xref:DatabentoDotNet.Dbn.Publishers> | `.Dbn` | Generated publisher, dataset and venue tables |
| <xref:DatabentoDotNet.Live> | `.Live` | `LiveClient`, `Subscription`, the gateway and its protocol types |
| <xref:DatabentoDotNet.Historical> | `.Historical` | `HistoricalClient` and its four subclients, and the parameter records they take |
| <xref:DatabentoDotNet.Historical.Json> | `.Historical` | Source-generated `JsonSerializerContext` types for the HTTP payloads |
| <xref:DatabentoDotNet.Reference> | `.Reference` | `ReferenceClient`, security master, corporate actions, adjustment factors, and the generated code tables |
| <xref:DatabentoDotNet.Reference.Json> | `.Reference` | Source-generated `JsonSerializerContext` types for the reference payloads |

The two `.Json` namespaces are public because source-generated serialization contexts have to be —
the generator emits public partial classes and the trimmer needs to reach them. Nothing in them is
meant to be called directly.

## Reading the record structs

The record types in <xref:DatabentoDotNet.Dbn> — <xref:DatabentoDotNet.Dbn.TradeMsg>,
<xref:DatabentoDotNet.Dbn.Mbp1Msg>, <xref:DatabentoDotNet.Dbn.OhlcvMsg> and the rest — document
fields whose types *are* the wire layout. A `ulong` timestamp field is a `ulong` because the wire
carries eight bytes there, and it stays one.

Two things about them that no single member's remarks can state, because both are properties of the
whole library:

- [Zero-Copy and Allocation](https://github.com/jerbersoft/databentodotnet/wiki/Zero-Copy-and-Allocation) —
  a <xref:DatabentoDotNet.Dbn.RecordRef> points into the read buffer and is valid until the next
  call on the decoder. Violating that reads stale bytes rather than throwing.
- [Timestamps and Prices](https://github.com/jerbersoft/databentodotnet/wiki/Timestamps-and-Prices) —
  why nothing here takes a `DateTime`, and the three sentinels that survive a naive conversion.
