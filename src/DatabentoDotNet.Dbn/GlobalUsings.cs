/*
 * Upstream's record type aliases.
 *
 * `record.rs` declares six `pub type` aliases (TbboMsg = Mbp1Msg, Bbo1SMsg/Bbo1MMsg = BboMsg,
 * TcbboMsg = Cmbp1Msg, Cbbo1SMsg/Cbbo1MMsg = CbboMsg); databento-cpp mirrors them with `using`.
 * They are pure aliases: one layout, several schema-facing names.
 *
 * These are `global using` aliases rather than wrapper structs on purpose. A wrapper would be a
 * distinct CLR type with its own layout to prove and would break `RecordRef.TryGet<T>()`
 * dispatch, which matches on `HasRType(rtype) && wireLength == T.WireSize` — two types with the
 * same rtype and the same size are exactly the ambiguity that rule cannot resolve. An alias adds
 * no type and no layout.
 *
 * The trade-off, stated plainly: C# aliases are compile-time and assembly-local, so unlike Rust
 * `pub type` or C++ `using` these names do NOT reach package consumers. Consumers write
 * `Mbp1Msg` and find the alias names documented in each struct's <remarks>. C# has no
 * consumer-visible type alias, so this is the whole of what the language offers short of
 * introducing real types.
 */

global using Bbo1MMsg = DatabentoDotNet.Dbn.BboMsg;
global using Bbo1SMsg = DatabentoDotNet.Dbn.BboMsg;
global using Cbbo1MMsg = DatabentoDotNet.Dbn.CbboMsg;
global using Cbbo1SMsg = DatabentoDotNet.Dbn.CbboMsg;
global using TbboMsg = DatabentoDotNet.Dbn.Mbp1Msg;
global using TcbboMsg = DatabentoDotNet.Dbn.Cmbp1Msg;
