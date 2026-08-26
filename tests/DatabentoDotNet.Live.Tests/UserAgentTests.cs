namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Tests for <see cref="UserAgent"/>, the <c>client=</c> string sent on authentication.
/// </summary>
public class UserAgentTests
{
    [Fact]
    public void Value_DoesNotClaimToBeTheVendorsOwnClient()
    {
        // "Databento/1.2.3" is what upstream's Rust client sends. Sending it from here would
        // attribute this library's traffic, and its bugs, to the vendor in their own logs.
        Assert.StartsWith("DatabentoDotNet/", UserAgent.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(" Rust ", UserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_NamesTheRuntimeAndThePlatform()
    {
        Assert.Contains(" .NET ", UserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_CarriesNoCharacterThatWouldBreakTheAuthLine()
    {
        // It goes into a pipe-delimited, newline-terminated line. Either character in it would
        // not corrupt the user agent — it would corrupt the message around it.
        Assert.DoesNotContain("|", UserAgent.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", UserAgent.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", UserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_DropsTheCommitHashSourceLinkAppendsToTheVersion()
    {
        // SourceLink writes '0.1.0-alpha+3f56f8f…' into the informational version. Forty
        // characters of no interest to a gateway.
        Assert.DoesNotContain("+", UserAgent.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_IsComputedOnce()
    {
        Assert.Same(UserAgent.Value, UserAgent.Value);
    }
}
