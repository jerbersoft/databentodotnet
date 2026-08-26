using DatabentoDotNet.Dbn.Enums;

namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Verifies <see cref="RTypeSchemaMapping"/> against upstream's <c>impl From&lt;Schema&gt; for
/// RType</c> and <c>RType::try_into_schema</c>, including the documented many-to-one asymmetry.
/// </summary>
public class RTypeSchemaMappingTests
{
    [Theory]
    [InlineData(Schema.Mbo, RType.Mbo)]
    [InlineData(Schema.Mbp1, RType.Mbp1)]
    [InlineData(Schema.Tbbo, RType.Mbp1)]
    [InlineData(Schema.Mbp10, RType.Mbp10)]
    [InlineData(Schema.Trades, RType.Mbp0)]
    [InlineData(Schema.Ohlcv1S, RType.Ohlcv1S)]
    [InlineData(Schema.Ohlcv1M, RType.Ohlcv1M)]
    [InlineData(Schema.Ohlcv1H, RType.Ohlcv1H)]
    [InlineData(Schema.Ohlcv1D, RType.Ohlcv1D)]
    [InlineData(Schema.OhlcvEod, RType.OhlcvEod)]
    [InlineData(Schema.Definition, RType.InstrumentDef)]
    [InlineData(Schema.Statistics, RType.Statistics)]
    [InlineData(Schema.Status, RType.Status)]
    [InlineData(Schema.Imbalance, RType.Imbalance)]
    [InlineData(Schema.Cmbp1, RType.Cmbp1)]
    [InlineData(Schema.Cbbo1S, RType.Cbbo1S)]
    [InlineData(Schema.Cbbo1M, RType.Cbbo1M)]
    [InlineData(Schema.Tcbbo, RType.Tcbbo)]
    [InlineData(Schema.Bbo1S, RType.Bbo1S)]
    [InlineData(Schema.Bbo1M, RType.Bbo1M)]
    public void ToRType_MatchesUpstreamMapping(Schema schema, RType expected)
    {
        Assert.Equal(expected, schema.ToRType());
    }

    [Fact]
    public void ToRType_Mbp1AndTbbo_BothMapToTheSameRType()
    {
        Assert.Equal(Schema.Mbp1.ToRType(), Schema.Tbbo.ToRType());
        Assert.Equal(RType.Mbp1, Schema.Tbbo.ToRType());
    }

    [Theory]
    [InlineData((byte)0x00, Schema.Trades)]
    [InlineData((byte)0x01, Schema.Mbp1)]
    [InlineData((byte)0x0A, Schema.Mbp10)]
    [InlineData((byte)0x20, Schema.Ohlcv1S)]
    [InlineData((byte)0x21, Schema.Ohlcv1M)]
    [InlineData((byte)0x22, Schema.Ohlcv1H)]
    [InlineData((byte)0x23, Schema.Ohlcv1D)]
    [InlineData((byte)0x24, Schema.OhlcvEod)]
    [InlineData((byte)0x12, Schema.Status)]
    [InlineData((byte)0x13, Schema.Definition)]
    [InlineData((byte)0x14, Schema.Imbalance)]
    [InlineData((byte)0x18, Schema.Statistics)]
    [InlineData((byte)0xA0, Schema.Mbo)]
    [InlineData((byte)0xB1, Schema.Cmbp1)]
    [InlineData((byte)0xC0, Schema.Cbbo1S)]
    [InlineData((byte)0xC1, Schema.Cbbo1M)]
    [InlineData((byte)0xC2, Schema.Tcbbo)]
    [InlineData((byte)0xC3, Schema.Bbo1S)]
    [InlineData((byte)0xC4, Schema.Bbo1M)]
    public void TryIntoSchema_MatchesUpstreamMapping(byte rtype, Schema expected)
    {
        Assert.True(RTypeSchemaMapping.TryIntoSchema(rtype, out var schema));
        Assert.Equal(expected, schema);
    }

    [Theory]
    [InlineData((byte)0x11)] // OhlcvDeprecated: predates per-cadence OHLCV rtypes.
    [InlineData((byte)0x15)] // Error: control/meta record, no associated schema.
    [InlineData((byte)0x16)] // SymbolMapping: control/meta record, no associated schema.
    [InlineData((byte)0x17)] // System: control/meta record, no associated schema.
    public void TryIntoSchema_RtypesWithNoSchema_ReturnFalse(byte rtype)
    {
        Assert.False(RTypeSchemaMapping.TryIntoSchema(rtype, out var schema));
        Assert.Equal(default, schema);
    }

    [Fact]
    public void TryIntoSchema_UndefinedByte_ReturnsFalseWithoutThrowing()
    {
        Assert.False(RTypeSchemaMapping.TryIntoSchema((byte)0x05, out var schema));
        Assert.Equal(default, schema);
    }

    [Fact]
    public void ToRType_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Schema)999).ToRType());
    }
}
