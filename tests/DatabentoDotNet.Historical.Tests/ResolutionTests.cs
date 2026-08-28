using DatabentoDotNet.Dbn;
using NodaTime;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="Resolution.ToSymbolMap"/> — the end of the <c>ALL_SYMBOLS</c> workflow,
/// where a resolution becomes something the decoder side can read records through.
/// </summary>
public sealed class ResolutionTests
{
    private static readonly LocalDate Start = new(2024, 1, 2);
    private static readonly LocalDate End = new(2024, 1, 5);

    /// <summary>
    /// Resolving raw symbols to instrument ids: the dictionary key is the text symbol and the
    /// interval carries the id, so the id is the side that gets parsed.
    /// </summary>
    [Fact]
    public void ToSymbolMap_ResolvingToInstrumentIds_KeysTheMapByTheIntervalsSymbol()
    {
        var map = ResolutionFor(
            SType.RawSymbol,
            SType.InstrumentId,
            ("ESH4", "17077"),
            ("ESM4", "5602")).ToSymbolMap();

        Assert.True(map.TryGetSymbol(Start, 17077, out var first));
        Assert.Equal("ESH4", first);
        Assert.True(map.TryGetSymbol(Start, 5602, out var second));
        Assert.Equal("ESM4", second);
    }

    /// <summary>
    /// Resolving instrument ids to raw symbols: the same two values, in the opposite roles.
    /// </summary>
    /// <remarks>
    /// <b>This is the half a single-direction implementation would get wrong, silently.</b> Both
    /// directions produce a map keyed by instrument id; what flips is <em>which</em> of the two
    /// values is the id. An implementation that always parsed the interval's symbol would throw
    /// here rather than mis-map — but one that always parsed the key would build a map from the
    /// text symbol's digits in the other direction, and only a test that asserts both catches
    /// which. Upstream branches on <c>stype_in</c> for exactly this reason
    /// (<c>symbology.rs:152</c>).
    /// </remarks>
    [Fact]
    public void ToSymbolMap_ResolvingFromInstrumentIds_KeysTheMapByTheDictionaryKey()
    {
        var map = ResolutionFor(
            SType.InstrumentId,
            SType.RawSymbol,
            ("17077", "ESH4"),
            ("5602", "ESM4")).ToSymbolMap();

        Assert.True(map.TryGetSymbol(Start, 17077, out var first));
        Assert.Equal("ESH4", first);
        Assert.True(map.TryGetSymbol(Start, 5602, out var second));
        Assert.Equal("ESM4", second);
    }

    /// <summary>
    /// A symbol that resolved to nothing contributes nothing, rather than failing the whole map.
    /// </summary>
    /// <remarks>
    /// It is in <see cref="Resolution.Mappings"/> with an empty interval list — the shape the real
    /// API returns for a not-found symbol — so the loop over its intervals simply does not run.
    /// The alternative, throwing, would make a single bad symbol in a large request unmappable in
    /// its entirety.
    /// </remarks>
    [Fact]
    public void ToSymbolMap_SkipsSymbolsThatResolvedToNothing()
    {
        var resolution = new Resolution
        {
            Mappings = new Dictionary<string, IReadOnlyList<MappingInterval>>(StringComparer.Ordinal)
            {
                ["ESH4"] = [new MappingInterval(Start, End, "17077")],
                ["NOTAREALSYMBOL"] = [],
            },
            Partial = [],
            NotFound = ["NOTAREALSYMBOL"],
            StypeIn = SType.RawSymbol,
            StypeOut = SType.InstrumentId,
        };

        var map = resolution.ToSymbolMap();

        // Three, not one: TsSymbolMap.Count is instrument-days, and the surviving interval spans
        // 2 to 5 January exclusive. The point is that the empty entry added none of them.
        Assert.Equal(3, map.Count);
        Assert.True(map.TryGetSymbol(Start, 17077, out var symbol));
        Assert.Equal("ESH4", symbol);
    }

    /// <summary>
    /// A value that has to be an instrument id but is not a number names itself in the exception.
    /// </summary>
    /// <remarks>
    /// Reachable from a valid request: asking for <c>stype_out=raw_symbol</c> from
    /// <c>stype_in=raw_symbol</c> resolves symbols to symbols, and neither side is then a number.
    /// A symbol map cannot represent that, so it fails loudly rather than mapping to a zero id.
    /// </remarks>
    [Fact]
    public void ToSymbolMap_WhenNeitherSideIsAnInstrumentId_ThrowsNamingTheValue()
    {
        var resolution = ResolutionFor(SType.RawSymbol, SType.RawSymbol, ("ESH4", "ESH4-ALIAS"));

        var thrown = Assert.Throws<FormatException>(resolution.ToSymbolMap);
        Assert.Contains("ESH4-ALIAS", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A negative number is not an instrument id either — instrument ids are <c>uint</c>, and
    /// parsing has to refuse a sign rather than wrap it.
    /// </summary>
    [Fact]
    public void ToSymbolMap_WithASignedValue_Throws()
    {
        var resolution = ResolutionFor(SType.RawSymbol, SType.InstrumentId, ("ESH4", "-1"));

        Assert.Throws<FormatException>(resolution.ToSymbolMap);
    }

    /// <summary>
    /// A resolution over <paramref name="pairs"/>, each spanning the whole test range.
    /// </summary>
    private static Resolution ResolutionFor(
        SType stypeIn,
        SType stypeOut,
        params (string Key, string Symbol)[] pairs) => new()
        {
            Mappings = pairs.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MappingInterval>)[new MappingInterval(Start, End, pair.Symbol)],
                StringComparer.Ordinal),
            Partial = [],
            NotFound = [],
            StypeIn = stypeIn,
            StypeOut = stypeOut,
        };
}
