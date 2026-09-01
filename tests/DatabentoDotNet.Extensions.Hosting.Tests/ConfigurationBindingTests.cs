using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// The configuration binding source generator does the binding, and this is the guard that keeps
/// it doing so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The load-bearing assertion here is that the library compiles at all</b>, and it is not made
/// in this file — it is made by <c>DatabentoDotNet.Extensions.Hosting</c> building.
/// <c>ConfigurationBinder.Bind</c>, <c>OptionsBuilder&lt;T&gt;.Bind</c>,
/// <c>BindConfiguration</c> and the reflection-based <c>Configure&lt;T&gt;</c> are annotated
/// <c>RequiresUnreferencedCode</c> and <c>RequiresDynamicCode</c>;
/// <c>$(ShippingProject)</c> turns on both analyzers and <c>TreatWarningsAsErrors</c> turns each
/// annotation into an error. Measured: with the generator on, 0 warnings; with it off, six errors
/// from three call sites.
/// </para>
/// <para>
/// What this file adds is the runtime half. A generator that compiles but binds the wrong shape
/// would pass the build and fail in a consumer's <c>appsettings.json</c>, so the cases below are
/// exactly the shapes §4 of the design uses: a nested object, a list of nested objects, and a list
/// of strings.
/// </para>
/// </remarks>
public class ConfigurationBindingTests
{
    private static IConfiguration Configuration(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        try
        {
            return new ConfigurationBuilder().AddJsonFile(path).Build();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Bind_OverTheDesignedShape_FillsEveryLevel()
    {
        var configuration = Configuration(
            """
            {
              "Databento": {
                "ApiKey": "db-0000000000000000000000000000",
                "Live": {
                  "equities": {
                    "Dataset": "EQUS.MINI",
                    "Subscriptions": [
                      { "Schema": "mbp-1", "StypeIn": "raw_symbol", "Symbols": ["AAPL", "MSFT"] }
                    ],
                    "Reconnect": {
                      "Enabled": true, "InitialDelay": "PT1S", "MaxDelay": "PT30S", "MaxAttempts": 10
                    }
                  }
                }
              }
            }
            """);

        var services = new ServiceCollection();
        services.AddOptions<LiveSessionOptions>("equities")
                .Bind(configuration.GetSection("Databento:Live:equities"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities");

        Assert.Equal("EQUS.MINI", options.Dataset);

        var subscription = Assert.Single(options.Subscriptions);
        Assert.Equal("mbp-1", subscription.Schema);
        Assert.Equal("raw_symbol", subscription.StypeIn);
        Assert.Equal(["AAPL", "MSFT"], subscription.Symbols);

        Assert.True(options.Reconnect.Enabled);
        Assert.Equal("PT1S", options.Reconnect.InitialDelay);
        Assert.Equal("PT30S", options.Reconnect.MaxDelay);
        Assert.Equal(10, options.Reconnect.MaxAttempts);
    }

    [Fact]
    public void Bind_OverAnAbsentSection_LeavesTheDefaults()
    {
        // A session declared in code with no configuration at all is a legal state — the lambda
        // overload may be supplying everything. The binder must not null out the defaults.
        var services = new ServiceCollection();
        services.AddOptions<LiveSessionOptions>("equities")
                .Bind(Configuration("{}").GetSection("Databento:Live:equities"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<LiveSessionOptions>>().Get("equities");

        Assert.Null(options.Dataset);
        Assert.Empty(options.Subscriptions);
        Assert.True(options.Reconnect.Enabled);
        Assert.Equal("PT1S", options.Reconnect.InitialDelay);
    }
}
