namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="HistoricalGateway"/> and <see cref="HistoricalGatewayExtensions"/>.
/// </summary>
public class HistoricalGatewayTests
{
    [Fact]
    public void Bo1_MapsToTheDocumentedHost()
    {
        // Uri normalises a bare authority to a root path on its own, so the gateway's base URL
        // carries a trailing slash even though the literal behind it does not. Verified, not
        // assumed — see HistoricalGateway's remarks.
        Assert.Equal(new Uri("https://hist.databento.com/"), HistoricalGateway.Bo1.ToUri());
    }

    [Fact]
    public void ToUri_ComposedWithASlug_ReachesTheVersionedRoute()
    {
        // The composition assertion the brief asks for: this is what keeps "no trailing slash in
        // the literal" honest, whatever the literal itself looks like. A request to any slug
        // arrives at v0/{slug}.
        var route = new Uri(HistoricalGateway.Bo1.ToUri(), "v0/metadata.list_datasets");

        Assert.Equal(new Uri("https://hist.databento.com/v0/metadata.list_datasets"), route);
    }

    [Fact]
    public void ToUri_AnUndefinedValue_ThrowsNamingTheParameter()
    {
        const HistoricalGateway undefined = (HistoricalGateway)99;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => undefined.ToUri());

        Assert.Equal("gateway", error.ParamName);
    }
}
