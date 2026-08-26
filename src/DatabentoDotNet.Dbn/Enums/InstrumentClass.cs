namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// The class of instrument.
/// </summary>
/// <remarks>
/// Char-valued: each variant's numeric value is its ASCII character code, and that character is
/// the only wire/text form this type has — there is no separate string representation. Use
/// <see cref="WireStrings.ToChar(InstrumentClass)"/> to read it as a <see cref="char"/>.
/// Upstream marks this type <c>#[non_exhaustive]</c>: Databento may add variants in a future
/// release without that being a breaking change. Do not treat an unrecognized byte as
/// necessarily invalid the way you would for e.g. <see cref="Schema"/>; it may simply be a
/// variant this port has not caught up to yet. This type has no upstream default variant.
/// </remarks>
public enum InstrumentClass : byte
{
    /// <summary>A bond.</summary>
    Bond = (byte)'B',

    /// <summary>A call option.</summary>
    Call = (byte)'C',

    /// <summary>A future.</summary>
    Future = (byte)'F',

    /// <summary>An index.</summary>
    Index = (byte)'I',

    /// <summary>A stock.</summary>
    Stock = (byte)'K',

    /// <summary>A spread composed of multiple instrument classes.</summary>
    MixedSpread = (byte)'M',

    /// <summary>A put option.</summary>
    Put = (byte)'P',

    /// <summary>A spread composed of futures.</summary>
    FutureSpread = (byte)'S',

    /// <summary>A spread composed of options.</summary>
    OptionSpread = (byte)'T',

    /// <summary>A foreign exchange spot.</summary>
    FxSpot = (byte)'X',

    /// <summary>A commodity being traded for immediate delivery.</summary>
    CommoditySpot = (byte)'Y',
}
