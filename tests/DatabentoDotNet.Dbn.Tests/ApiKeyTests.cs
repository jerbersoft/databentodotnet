namespace DatabentoDotNet.Dbn.Tests;

/// <summary>
/// Tests for <see cref="ApiKey"/>: the validation, and the redaction.
/// </summary>
/// <remarks>
/// The redaction tests are not cosmetic. An API key reaches a log file, a bug report, or a crash
/// dump through exactly one route — something formatted the object holding it — so
/// <see cref="ApiKey.ToString"/> and every message this type produces are asserted to contain no
/// more of the key than its bucket id.
/// </remarks>
public class ApiKeyTests
{
    private const string ValidKey = "db-0123456789abcdefghijklmnopqrs";

    [Fact]
    public void Constructor_AValidKey_KeepsItAndExposesItsBucketId()
    {
        var key = new ApiKey(ValidKey);

        Assert.Equal(ValidKey, key.Value);
        Assert.Equal("opqrs", key.BucketId);
        Assert.Equal(ApiKey.BucketIdLength, key.BucketId.Length);
        Assert.Equal(ValidKey[^ApiKey.BucketIdLength..], key.BucketId);
    }

    [Fact]
    public void ValidKey_IsExactlyTheLengthTheTypeDeclares()
    {
        Assert.Equal(ApiKey.Length, ValidKey.Length);
    }

    [Fact]
    public void ToString_ElidesEverythingButTheBucketId()
    {
        var rendered = new ApiKey(ValidKey).ToString();

        Assert.Equal("…opqrs", rendered);
        Assert.DoesNotContain(ValidKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidKey[..(ApiKey.Length - ApiKey.BucketIdLength)], rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ThePlaceholderFromTheDocumentation_SaysSo()
    {
        var error = Assert.Throws<ArgumentException>(() => new ApiKey(ApiKey.Placeholder));

        // "expected 32 characters, got 13" would send a reader hunting for a typo instead of for
        // the line they forgot to fill in.
        Assert.Contains("placeholder", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Constructor_AKeyOfTheWrongLength_Throws(string key)
    {
        var error = Assert.Throws<ArgumentException>(() => new ApiKey(key));

        Assert.Contains($"exactly {ApiKey.Length} characters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_AKeyOneCharacterTooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ApiKey(ValidKey + "x"));
    }

    [Fact]
    public void Constructor_ANonAsciiKey_ThrowsWithoutQuotingTheKey()
    {
        var key = ValidKey[..^1] + "é";

        var error = Assert.Throws<ArgumentException>(() => new ApiKey(key));

        Assert.Contains("ASCII", error.Message, StringComparison.Ordinal);

        // Upstream logs the whole key on this path (lib.rs:250). An invalid key is still a key,
        // and very often a valid key for a different account.
        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidKey[..8], error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiKey(null!));
    }

    // TheHarnessTestKey_IsAValidApiKey moved to MockLiveGatewayTests (DatabentoDotNet.Live.Tests)
    // in #32: it exercises MockLiveGateway.TestApiKey, a live-only test fixture that this project
    // has no reason to reference, and ApiKey no longer lives next to it.
}
