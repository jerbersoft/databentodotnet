/*
 * Symbols, SymbolsKind, ApiKey and UserAgent moved to DatabentoDotNet.Dbn/Common/ in #32, in the
 * shared DatabentoDotNet root namespace, so the historical client can use them without
 * duplicating public API. This is the one file that absorbs the move for every consumer in this
 * project, rather than adding a `using DatabentoDotNet;` to each of them individually.
 *
 * Not a template for DatabentoDotNet.Dbn/GlobalUsings.cs, which serves a different purpose
 * (record type aliases local to that project).
 */

global using DatabentoDotNet;
