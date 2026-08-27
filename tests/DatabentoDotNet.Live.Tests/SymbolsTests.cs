namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="Symbols"/>: the three forms, the 500-symbol chunk boundary, and the
/// validation that happens when a set is built rather than when it is sent.
/// </summary>
/// <remarks>
/// The chunk boundary is asserted here, in isolation from the socket, and again in
/// <see cref="LiveClientSubscriptionTests"/> against the mock gateway. Both are worth having: the
/// gateway proves the client sends what it chunked, and these prove the chunking is right in the
/// first place — a client that chunked at 501 and a gateway that accepted 501 would agree with
/// each other and be wrong together.
/// </remarks>
public class SymbolsTests
{
    [Fact]
    public void All_IsOneChunkOfTheSentinelValue()
    {
        Assert.Equal(SymbolsKind.All, Symbols.All.Kind);
        Assert.Equal(1, Symbols.All.ChunkCount);
        Assert.Equal(["ALL_SYMBOLS"], Symbols.All.ToChunks());
    }

    [Fact]
    public void From_KeepsTheSymbolsInTheOrderGiven()
    {
        var symbols = Symbols.From(["MSFT", "AAPL", "NVDA"]);

        Assert.Equal(SymbolsKind.Symbols, symbols.Kind);
        Assert.Equal(3, symbols.Count);
        Assert.Equal(["MSFT,AAPL,NVDA"], symbols.ToChunks());
    }

    [Fact]
    public void FromIds_RendersEachIdAsItsDecimalForm()
    {
        var symbols = Symbols.FromIds([1u, 4_294_967_295u, 0u]);

        Assert.Equal(SymbolsKind.Ids, symbols.Kind);
        Assert.Equal(["1,4294967295,0"], symbols.ToChunks());
    }

    [Fact]
    public void From_ASingleSymbol_IsTheSameAsAOneElementList()
    {
        Assert.Equal(Symbols.From(["AAPL"]), Symbols.From("AAPL"));
        Assert.Equal(Symbols.FromIds([7u]), Symbols.FromIds(7u));
    }

    // ---------------------------------------------------------------- The 500-symbol boundary

    [Theory]
    [InlineData(1, 1)]
    [InlineData(499, 1)]
    [InlineData(500, 1)]
    [InlineData(501, 2)]
    [InlineData(1000, 2)]
    [InlineData(1001, 3)]
    public void ToChunks_SplitsAtExactlyFiveHundred(int count, int expectedChunks)
    {
        var symbols = Symbols.From(Enumerable.Range(0, count).Select(i => $"SYM{i}"));
        var chunks = symbols.ToChunks();

        Assert.Equal(expectedChunks, symbols.ChunkCount);
        Assert.Equal(expectedChunks, chunks.Length);

        // Every chunk but the last is full; the last holds the remainder. Off-by-one here is what
        // silently drops a symbol from a real subscription.
        for (var i = 0; i < chunks.Length - 1; i++)
        {
            Assert.Equal(Symbols.ChunkSize, chunks[i].Split(',').Length);
        }

        var remainder = count % Symbols.ChunkSize;
        Assert.Equal(remainder == 0 ? Symbols.ChunkSize : remainder, chunks[^1].Split(',').Length);
    }

    [Fact]
    public void ToChunks_LosesNoSymbolAndReordersNothing()
    {
        var expected = Enumerable.Range(0, 1001).Select(i => $"SYM{i}").ToArray();

        var rejoined = Symbols.From(expected).ToChunks().SelectMany(chunk => chunk.Split(','));

        Assert.Equal(expected, rejoined);
    }

    // --------------------------------------------------------------------------- Rejections

    [Fact]
    public void From_AnEmptyList_IsRejectedRatherThanChunkedIntoNothing()
    {
        // Upstream underflows here: chunks(500) of an empty vec yields no chunks, and
        // `symbol_chunks.len() - 1` is then usize::MAX.
        var exception = Assert.Throws<ArgumentException>(() => Symbols.From([]));
        Assert.Contains("at least one symbol", exception.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Symbols.FromIds([]));
    }

    [Theory]
    [InlineData(",")]
    [InlineData("|")]
    [InlineData("=")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void From_ASymbolCarryingALineProtocolCharacter_IsRejected(string character)
    {
        // Each of these would produce a different well-formed subscription rather than a rejected
        // one: ',' adds a symbol, '|' adds a field, '=' moves the field boundary, and a newline
        // ends the message early.
        var exception = Assert.Throws<ArgumentException>(() => Symbols.From($"AA{character}PL"));

        Assert.Contains("Symbol 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void From_ReportsWhichSymbolInTheListIsWrong()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Symbols.From(["AAPL", "MSFT", "NVDA|extra"]));

        Assert.Contains("Symbol 2", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void From_AnEmptyOrBlankSymbol_IsRejected(string symbol)
    {
        Assert.Throws<ArgumentException>(() => Symbols.From(symbol));
    }

    [Fact]
    public void From_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Symbols.From((IEnumerable<string>)null!));
        Assert.Throws<ArgumentNullException>(() => Symbols.FromIds((IEnumerable<uint>)null!));
    }

    // ---------------------------------------------------------------------- The default value

    [Fact]
    public void Default_NamesNothingAndRefusesToChunk()
    {
        var symbols = default(Symbols);

        Assert.Equal(SymbolsKind.None, symbols.Kind);
        Assert.Equal(0, symbols.Count);
        Assert.Equal(0, symbols.ChunkCount);
        Assert.Throws<InvalidOperationException>(() => symbols.ToChunks());
    }

    // --------------------------------------------------------------------------- Value equality

    [Fact]
    public void Equality_IsByKindThenBySymbolsInOrder()
    {
        Assert.Equal(Symbols.From(["A", "B"]), Symbols.From(["A", "B"]));
        Assert.NotEqual(Symbols.From(["A", "B"]), Symbols.From(["B", "A"]));
        Assert.NotEqual(Symbols.From(["A"]), Symbols.All);

        // Same wire text, different meaning: stype_in decides how "1" is read, and a set of ids
        // must not compare equal to a set of symbols that happens to spell the same thing.
        Assert.NotEqual(Symbols.From(["1"]), Symbols.FromIds([1u]));

        Assert.Equal(Symbols.From(["A"]).GetHashCode(), Symbols.From(["A"]).GetHashCode());
        Assert.True(Symbols.From("A") == Symbols.From("A"));
        Assert.True(Symbols.From("A") != Symbols.From("B"));
    }

    [Fact]
    public void ToString_ElidesALongSetRatherThanDumpingIt()
    {
        Assert.Equal("ALL_SYMBOLS", Symbols.All.ToString());
        Assert.Equal("A, B", Symbols.From(["A", "B"]).ToString());
        Assert.Equal("Symbols(none)", default(Symbols).ToString());

        var many = Symbols.From(Enumerable.Range(0, 600).Select(i => $"S{i}")).ToString();
        Assert.Contains("(600 symbols)", many, StringComparison.Ordinal);
        Assert.DoesNotContain("S599", many, StringComparison.Ordinal);
    }
}
