namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="TimeseriesFixture"/> — the harness's own oracle, checked before anything is
/// asserted against it.
/// </summary>
/// <remarks>
/// <para>
/// Every count in <c>TimeseriesClientTests</c> and <c>TimeseriesAllocationTests</c> rests on
/// <see cref="TimeseriesFixture.RecordCount"/>. A vendored fixture that changed upstream, or a link
/// that stopped copying, would leave those assertions comparing two numbers that moved together —
/// green, and measuring nothing. These tests are what makes that loud.
/// </para>
/// <para>
/// The record walk here is deliberately the crudest possible reading of DBN: step forward by the
/// first byte of each record times four. It shares no code with the decoder it exists to check.
/// </para>
/// </remarks>
public sealed class TimeseriesFixtureTests
{
    /// <summary>Both linked fixtures exist in the output directory and are non-empty.</summary>
    [Fact]
    public void TheLinkedFixtures_AreCopiedToTheOutputDirectory()
    {
        Assert.True(
            File.Exists(Path.Combine(TimeseriesFixture.Directory, TimeseriesFixture.CompressedName)),
            $"{TimeseriesFixture.CompressedName} is missing — the csproj link stopped copying it.");
        Assert.True(
            File.Exists(Path.Combine(TimeseriesFixture.Directory, TimeseriesFixture.PlainName)),
            $"{TimeseriesFixture.PlainName} is missing — the csproj link stopped copying it.");
    }

    /// <summary>
    /// Both fixtures hold exactly <see cref="TimeseriesFixture.RecordCount"/> records, and they are
    /// the DBN versions the fixture's documentation claims.
    /// </summary>
    [Fact]
    public void BothFixtures_HoldTheDocumentedRecordCountAndVersion()
    {
        var plain = TimeseriesFixture.Plain();
        var decompressed = TimeseriesFixture.Decompressed();

        Assert.Equal(TimeseriesFixture.RecordCount, WalkRecords(plain));
        Assert.Equal(TimeseriesFixture.RecordCount, WalkRecords(decompressed));

        Assert.Equal("DBN"u8.ToArray(), plain[..3]);
        Assert.Equal("DBN"u8.ToArray(), decompressed[..3]);
        Assert.Equal(2, plain[3]);
        Assert.Equal(3, decompressed[3]);
    }

    /// <summary>
    /// <see cref="TimeseriesFixture.Repeating"/> reports a record count a naive walk agrees with,
    /// and produces a stream comfortably larger than the decoder's default buffer.
    /// </summary>
    [Fact]
    public void Repeating_ProducesTheRecordCountItClaims()
    {
        var (bytes, records) = TimeseriesFixture.Repeating(1_000);

        Assert.Equal(TimeseriesFixture.RecordCount * 1_000, records);
        Assert.Equal(records, WalkRecords(bytes));
        Assert.True(
            bytes.Length > 64 * 1024,
            $"A stream of {bytes.Length} bytes does not exceed the 64 KB decode buffer, so it would "
            + "never force a refill.");
    }

    /// <summary>
    /// <see cref="TimeseriesFixture.TruncatedMidRecord"/> really does cut inside a record: the
    /// surviving whole records are one fewer than the fixture holds, and the leftover bytes are a
    /// partial record rather than none.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(47)]
    public void TruncatedMidRecord_LeavesAPartialRecordBehind(int missingBytes)
    {
        var (body, wholeRecords) = TimeseriesFixture.TruncatedMidRecord(missingBytes);

        Assert.Equal(TimeseriesFixture.RecordCount - 1, wholeRecords);

        using var decompressor = new ZstdSharp.Decompressor();
        var stream = decompressor.Unwrap(body).ToArray();
        var records = TimeseriesFixture.RecordsOf(stream);

        // Walk the whole records, then confirm what is left over is a non-empty remainder shorter
        // than the record it belongs to.
        var offset = 0;
        var whole = 0;
        while (offset + (records[offset] * 4) <= records.Length)
        {
            offset += records[offset] * 4;
            whole++;
        }

        Assert.Equal(wholeRecords, whole);

        // What is left is the front of the last record, short by exactly what was withheld.
        var declaredLength = records[offset] * 4;
        var remainder = records.Length - offset;
        Assert.Equal(declaredLength - missingBytes, remainder);
        Assert.InRange(remainder, 1, declaredLength - 1);
    }

    /// <summary>
    /// A cut that removes a whole record is refused: that is a shorter stream, not a truncated one,
    /// and a test built on it would assert nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    public void TruncatedMidRecord_RefusesACutThatIsNotMidRecord(int missingBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeseriesFixture.TruncatedMidRecord(missingBytes));

    /// <summary>
    /// Steps through a DBN stream's records the crudest way the format allows, sharing nothing with
    /// the decoder.
    /// </summary>
    private static int WalkRecords(ReadOnlySpan<byte> stream)
    {
        var records = TimeseriesFixture.RecordsOf(stream);
        var offset = 0;
        var count = 0;

        while (offset < records.Length)
        {
            offset += records[offset] * 4;
            count++;
        }

        Assert.Equal(records.Length, offset);
        return count;
    }
}
