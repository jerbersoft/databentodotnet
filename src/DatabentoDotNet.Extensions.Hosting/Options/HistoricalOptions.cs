namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>Configuration for the historical and reference clients, which share one transport.</summary>
public sealed class HistoricalOptions
{
    /// <summary>The API key, or <see langword="null"/> to use <see cref="DatabentoOptions.ApiKey"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// A base URL to send requests to instead of the gateway's, or <see langword="null"/> for the
    /// gateway. For a proxy or a test harness.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Text identifying the application, appended to this library's <c>User-Agent</c>.</summary>
    public string? UserAgentExtension { get; set; }

    /// <summary>
    /// How long a pooled connection may be reused before it is replaced, as an ISO-8601 duration.
    /// Defaults to <c>PT5M</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the reason the package registers an <c>IHttpClientFactory</c> handler at all.</b>
    /// <see cref="System.Net.Http.HttpClient"/>'s own handler leaves
    /// <c>PooledConnectionLifetime</c> infinite, so a singleton in a host that stays up for weeks
    /// keeps talking to whatever address <c>hist.databento.com</c> resolved to on its first
    /// request. Five minutes is what the .NET documentation recommends for a long-lived client.
    /// </para>
    /// <para>
    /// A <see langword="string"/> because <c>T:System.TimeSpan</c> is banned as a type repo-wide
    /// and NodaTime's <c>Duration</c> has nothing for a binder to fill. ISO-8601 is unambiguous
    /// across locales, which <c>InvariantGlobalization</c> makes a live concern.
    /// </para>
    /// </remarks>
    public string? PooledConnectionLifetime { get; set; }
}
