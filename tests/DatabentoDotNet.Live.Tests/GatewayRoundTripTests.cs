namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// <see cref="GatewayRoundTrip"/> driven by <see cref="MockLiveGateway"/> (#83).
/// </summary>
/// <remarks>
/// <para>
/// <b>The mock cannot say what the round trip is, and that is not what these check.</b> Over
/// loopback the figure is about loopback. What a mock settles completely is everything around the
/// number: that each sample opens its own connection, that both legs are timed and kept apart,
/// that the endpoint is captured, and that the report says what was measured. #65 split its
/// measurement from its session for this reason, and this follows it — debugging a report format
/// during a run against a real gateway would be the wrong way round.
/// </para>
/// <para>
/// These run on every <c>dotnet test</c>: no key, no category, no gate.
/// </para>
/// </remarks>
public class GatewayRoundTripTests
{
    private const string DatasetName = "GLBX.MDP3";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MeasureAsync_CollectsBothLegsOfEverySample()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var measurement = await MeasureAsync(gateway, samples: 3);

        Assert.Equal(3, measurement.Samples);
        Assert.Equal(3, measurement.Connect.Count);
        Assert.Equal(3, measurement.Handshake.Count);
    }

    [Fact]
    public async Task MeasureAsync_TimesForwards()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var measurement = await MeasureAsync(gateway, samples: 3);

        // Both stamps of each figure come from one monotonic source, so a negative here is the
        // stopwatch running backwards rather than a fast network — the same instrument check
        // RealGatewayLatencyTests makes on its drain series, for the same reason.
        Assert.All(measurement.Connect, nanoseconds => Assert.True(nanoseconds >= 0));
        Assert.All(measurement.Handshake, nanoseconds => Assert.True(nanoseconds >= 0));
    }

    [Fact]
    public async Task MeasureAsync_OpensAFreshConnectionPerSample()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        // The gateway side is armed once per sample and every one of them completes. A run that
        // reused a single connection would leave the second and third handshakes waiting for an
        // accept that never comes, and this would time out rather than pass.
        var handshakes = new List<Task<IReadOnlyDictionary<string, string>>>();
        await GatewayRoundTrip.MeasureAsync(
            3,
            async (index, token) =>
            {
                if (index > 0)
                {
                    await handshakes[index - 1];
                    await gateway.CloseAsync();
                }

                handshakes.Add(gateway.AuthenticateAsync(cancellationToken: token));
                return Client(gateway);
            },
            Cancel);

        var completed = await Task.WhenAll(handshakes);
        Assert.Equal(3, completed.Length);
        Assert.All(completed, fields => Assert.True(fields.ContainsKey("auth")));
    }

    [Fact]
    public async Task MeasureAsync_CapturesTheEndpointItConnectedTo()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var measurement = await MeasureAsync(gateway, samples: 1);

        Assert.Equal(gateway.Address, measurement.Endpoint);
    }

    [Fact]
    public async Task MeasureAsync_WithoutASample_IsRejectedRatherThanReportingAnEmptyTable()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => GatewayRoundTrip.MeasureAsync(
                0, (_, _) => Task.FromResult(Client(gateway)), Cancel));
    }

    [Fact]
    public async Task Render_NamesTheEndpointTheSampleCountAndBothLegs()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var measurement = await MeasureAsync(gateway, samples: 2);
        var report = measurement.Render(DatasetName);

        Assert.Contains(DatasetName, report, StringComparison.Ordinal);
        Assert.Contains(gateway.Address.ToString(), report, StringComparison.Ordinal);
        Assert.Contains("2 connection(s)", report, StringComparison.Ordinal);
        Assert.Contains("connect (TCP handshake)", report, StringComparison.Ordinal);
        Assert.Contains("authenticate (greeting + CRAM)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_SaysThatHalvingTheRoundTripIsAnAssumptionRatherThanAMeasurement()
    {
        await using var gateway = new MockLiveGateway(DatasetName);

        var report = (await MeasureAsync(gateway, samples: 1)).Render(DatasetName);

        // The one sentence in the report that stops a reader quoting RTT/2 as a measured one-way
        // latency. #83 exists because that distinction is the whole point; losing it in an edit
        // would leave a report that reads exactly as confident and means something weaker.
        Assert.Contains("must not be reported as one", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs <paramref name="samples"/> measured connections against <paramref name="gateway"/>.
    /// </summary>
    /// <remarks>
    /// <b>The gateway side is cycled between samples, and only the mock needs that.</b>
    /// <see cref="MockLiveGateway"/> holds one connection at a time and refuses a second accept
    /// until <c>CloseAsync</c> — a real gateway accepts as many as it is offered. So the awkwardness
    /// belongs here rather than in <see cref="GatewayRoundTrip"/>, whose contract is simply that
    /// every sample opens its own connection.
    /// </remarks>
    private static async Task<GatewayRoundTrip> MeasureAsync(MockLiveGateway gateway, int samples)
    {
        var handshakes = new List<Task<IReadOnlyDictionary<string, string>>>();

        var measurement = await GatewayRoundTrip.MeasureAsync(
            samples,
            async (index, token) =>
            {
                if (index > 0)
                {
                    await handshakes[index - 1];
                    await gateway.CloseAsync();
                }

                handshakes.Add(gateway.AuthenticateAsync(cancellationToken: token));
                return Client(gateway);
            },
            Cancel);

        await Task.WhenAll(handshakes);
        return measurement;
    }

    private static LiveClient Client(MockLiveGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockLiveGateway.TestApiKey),
        Dataset = gateway.Dataset,
        Gateway = gateway.Address,
    };
}
