using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet;

/// <summary>
/// The set of symbols a live subscription or a historical query covers: every symbol in the
/// dataset, a list of symbols in some symbology, or a list of numeric instrument IDs.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Symbols</c> sum type (<c>databento-rs/src/lib.rs</c>): <c>All</c>,
/// <c>Symbols(Vec&lt;String&gt;)</c>, <c>Ids(Vec&lt;u32&gt;)</c>. A <see langword="readonly"/>
/// <see langword="struct"/> with factories rather than a set of <see langword="string"/>
/// overloads on <c>Subscribe</c>, because an overload set cannot express "all symbols" — the
/// wire spells that as the literal <see cref="AllWireValue"/>, and a caller who passes that
/// string by hand has written a magic value the type system cannot check. PORTING.md §2.
/// </para>
/// <para>
/// <b>Symbols are validated when the set is built, not when it is sent.</b> A subscription line
/// is <c>|</c>-separated with <c>=</c> between key and value and terminated by <c>\n</c>, so a
/// symbol carrying any of those characters does not produce a rejected subscription — it
/// produces a <em>different, well-formed</em> one, silently. That is the failure mode this
/// library exists to turn back into an exception, and the earliest place to do it is here, where
/// the offending symbol is still in the caller's hand.
/// </para>
/// <para>
/// <b>An empty set is rejected, where upstream panics.</b> Upstream's <c>subscribe</c> takes
/// <c>symbol_chunks.len() - 1</c> to find the last chunk; for an empty symbol list
/// <c>chunks(500)</c> yields no chunks at all, and that subtraction underflows — a panic in a
/// debug build and an enormous index in a release one. There is no meaningful empty
/// subscription, so the set cannot be built in the first place.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Symbols two = Symbols.From(["AAPL", "MSFT"]);   // raw symbols, the common case
/// Symbols one = Symbols.From("ESH4");
/// Symbols ids = Symbols.FromIds([12345u, 67890u]);   // instrument ids
/// Symbols everything = Symbols.All;                  // the whole dataset
///
/// Console.WriteLine(two.ToApiString());   // AAPL,MSFT
/// Console.WriteLine(two.ChunkCount);      // 1 — the gateway takes 500 symbols per message
///
/// // Rejected here, while the offending symbol is still in your hand. The subscription line is
/// // '|'-separated, so a symbol carrying one would not produce a rejected subscription — it would
/// // produce a different, well-formed one, silently.
/// Symbols.From("AA|PL");   // throws ArgumentException
/// </code>
/// </example>
public readonly struct Symbols : IEquatable<Symbols>
{
    /// <summary>The wire value that means every symbol in the dataset.</summary>
    public const string AllWireValue = "ALL_SYMBOLS";

    /// <summary>
    /// The most symbols the gateway accepts in one subscription message. A larger set is split
    /// across several messages, only the last of which is marked <c>is_last=1</c>.
    /// </summary>
    /// <remarks>
    /// The boundary is exact and getting it wrong is invisible from the outside: the gateway
    /// accepts a 501-symbol message without complaint and simply never subscribes the last
    /// symbol. Nothing on the wire says so.
    /// </remarks>
    public const int ChunkSize = 500;

    /// <summary>The characters a symbol may not contain, because the line protocol uses them.</summary>
    private static readonly SearchValues<char> Forbidden = SearchValues.Create(",|=\n\r");

    private readonly ImmutableArray<string> _values;

    private Symbols(SymbolsKind kind, ImmutableArray<string> values)
    {
        Kind = kind;
        _values = values;
    }

    /// <summary>Which of the three forms this set takes.</summary>
    /// <remarks>
    /// <see cref="SymbolsKind.None"/> for a <see langword="default"/> value, which is not a usable
    /// set — both <see cref="ToChunks"/> and <see cref="ToApiString"/> refuse it outright, and the
    /// live client's <c>Subscription.Symbols</c> is <see langword="required"/> so one cannot
    /// arrive by omission.
    /// </remarks>
    public SymbolsKind Kind { get; }

    /// <summary>Every symbol in the dataset.</summary>
    public static Symbols All { get; } = new(SymbolsKind.All, [AllWireValue]);

    /// <summary>How many symbols this set names, or <c>1</c> for <see cref="All"/>.</summary>
    public int Count => _values.IsDefault ? 0 : _values.Length;

    /// <summary>
    /// How many subscription messages this set takes: one per <see cref="ChunkSize"/> symbols,
    /// and always one for <see cref="All"/>.
    /// </summary>
    public int ChunkCount => Kind switch
    {
        SymbolsKind.None => 0,
        SymbolsKind.All => 1,
        _ => (Count + ChunkSize - 1) / ChunkSize,
    };

    /// <summary>A set naming one symbol.</summary>
    /// <param name="symbol">The symbol, in whatever symbology the subscription's <c>stype_in</c> names.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is empty, white space, or carries a character the line protocol uses.</exception>
    public static Symbols From(string symbol) => From([symbol]);

    /// <summary>A set naming several symbols, in the order given.</summary>
    /// <param name="symbols">
    /// The symbols, in whatever symbology the subscription's <c>stype_in</c> names. Order is kept:
    /// it decides which chunk each symbol lands in, and the gateway echoes it back in error
    /// messages.
    /// </param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="symbols"/> is empty, or one of them is empty, white space, or carries a
    /// character the line protocol uses.
    /// </exception>
    public static Symbols From(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var builder = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        foreach (var symbol in symbols)
        {
            builder.Add(Validate(symbol, index++));
        }

        if (builder.Count == 0)
        {
            throw new ArgumentException(
                "A subscription must name at least one symbol; use Symbols.All to subscribe to "
                + "the whole dataset.",
                nameof(symbols));
        }

        return new Symbols(SymbolsKind.Symbols, builder.ToImmutable());
    }

    /// <summary>A set naming one numeric instrument ID.</summary>
    /// <param name="instrumentId">The instrument ID. Pair it with <see cref="SType.InstrumentId"/>.</param>
    /// <returns>The set.</returns>
    public static Symbols FromIds(uint instrumentId) => FromIds([instrumentId]);

    /// <summary>A set naming several numeric instrument IDs, in the order given.</summary>
    /// <param name="instrumentIds">The instrument IDs. Pair them with <see cref="SType.InstrumentId"/>.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instrumentIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instrumentIds"/> is empty.</exception>
    public static Symbols FromIds(IEnumerable<uint> instrumentIds)
    {
        ArgumentNullException.ThrowIfNull(instrumentIds);

        // Rendered to text here rather than at send time: an instrument ID cannot carry a
        // forbidden character, so the two forms differ only in how they are built and this keeps
        // one storage shape and one chunker for both.
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var id in instrumentIds)
        {
            builder.Add(id.ToString(CultureInfo.InvariantCulture));
        }

        if (builder.Count == 0)
        {
            throw new ArgumentException(
                "A subscription must name at least one instrument ID; use Symbols.All to "
                + "subscribe to the whole dataset.",
                nameof(instrumentIds));
        }

        return new Symbols(SymbolsKind.Ids, builder.ToImmutable());
    }

    /// <summary>
    /// The <c>symbols=</c> values this set produces, one per subscription message: comma-separated
    /// runs of at most <see cref="ChunkSize"/> symbols, or a single <see cref="AllWireValue"/>.
    /// </summary>
    /// <returns>The chunks, in order. Never empty.</returns>
    /// <exception cref="InvalidOperationException">This is a <see langword="default"/> value.</exception>
    public ImmutableArray<string> ToChunks()
    {
        if (Kind == SymbolsKind.None)
        {
            throw new InvalidOperationException(
                "This is a default Symbols value, which names nothing. Build one with "
                + "Symbols.All, Symbols.From, or Symbols.FromIds.");
        }

        if (Kind == SymbolsKind.All)
        {
            return [AllWireValue];
        }

        var chunks = ImmutableArray.CreateBuilder<string>(ChunkCount);
        for (var start = 0; start < _values.Length; start += ChunkSize)
        {
            var length = Math.Min(ChunkSize, _values.Length - start);

            // AsSpan, not (array, start, length): string.Join has no (char, IEnumerable<T>, int,
            // int) overload, so that call binds to Join(char, params object?[]) and joins the
            // array's ToString with "0" and "500". It compiles, and the gateway is the only thing
            // that notices.
            chunks.Add(string.Join(',', _values.AsSpan().Slice(start, length)));
        }

        return chunks.ToImmutable();
    }

    /// <summary>
    /// The <c>symbols=</c> value the historical HTTP API takes: every symbol in the set, joined
    /// with commas into a single string, with no chunking.
    /// </summary>
    /// <remarks>
    /// Port of upstream's <c>Symbols::to_api_string()</c> (<c>databento-rs/src/lib.rs</c>,
    /// called from <c>historical/symbology.rs</c> and <c>historical/timeseries.rs</c>). Unlike
    /// <see cref="ToChunks"/>, this never splits: <see cref="ChunkSize"/> is a live
    /// line-protocol limit, and an HTTP form field carries no such restriction, so it must never
    /// be chunked here even for a set with more than <see cref="ChunkSize"/> symbols.
    /// </remarks>
    /// <returns>The rendered value, or <see cref="AllWireValue"/> for <see cref="All"/>.</returns>
    /// <exception cref="InvalidOperationException">This is a <see langword="default"/> value.</exception>
    public string ToApiString()
    {
        if (Kind == SymbolsKind.None)
        {
            throw new InvalidOperationException(
                "This is a default Symbols value, which names nothing. Build one with "
                + "Symbols.All, Symbols.From, or Symbols.FromIds.");
        }

        // All has _values == [AllWireValue], so joining it here needs no special case the way
        // ToChunks needs one to avoid chunking a single sentinel string.
        return string.Join(',', _values.AsSpan());
    }

    /// <summary>The symbols this set names, in order, in their wire spelling.</summary>
    /// <returns>The symbols, or a single <see cref="AllWireValue"/> for <see cref="All"/>.</returns>
    public ImmutableArray<string> ToArray() => _values.IsDefault ? [] : _values;

    /// <inheritdoc/>
    public bool Equals(Symbols other) =>
        Kind == other.Kind && ToArray().SequenceEqual(other.ToArray(), StringComparer.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Symbols other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        foreach (var value in ToArray())
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when the two name the same symbols in the same order.</returns>
    public static bool operator ==(Symbols left, Symbols right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(Symbols left, Symbols right) => !left.Equals(right);

    /// <summary>A short description, for diagnostics. Long sets are elided rather than dumped.</summary>
    /// <returns>The description.</returns>
    public override string ToString() => Kind switch
    {
        SymbolsKind.None => "Symbols(none)",
        SymbolsKind.All => AllWireValue,
        _ when Count <= 8 => string.Join(", ", _values.AsSpan()),
        _ => $"{string.Join(", ", _values.AsSpan()[..8])}, … ({Count} symbols)",
    };

    private static string Validate(string symbol, int index)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException(
                $"Symbol {index} is null, empty, or white space.",
                nameof(symbol));
        }

        var offending = symbol.AsSpan().IndexOfAny(Forbidden);
        if (offending >= 0)
        {
            // Naming the character and its position matters more than usual here: the caller
            // almost certainly built this list by splitting or joining something, and a stray
            // comma inside one entry is invisible when the list is printed.
            var character = symbol[offending];
            var described = character switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                _ => character.ToString(),
            };

            throw new ArgumentException(
                $"Symbol {index}, '{symbol}', carries '{described}' at position {offending}. The "
                + "live gateway's subscription line separates fields with '|', keys from values "
                + "with '=', symbols with ',', and lines with a newline, so a symbol containing "
                + "any of those would change the meaning of the message rather than be rejected "
                + "by the gateway.",
                nameof(symbol));
        }

        return symbol;
    }
}
