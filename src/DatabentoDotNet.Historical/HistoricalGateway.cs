namespace DatabentoDotNet.Historical;

/// <summary>
/// A Databento historical API gateway.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>HistoricalGateway</c> (<c>historical.rs:24-40</c>), which has exactly one
/// member today. A second gateway, if Databento ever opens one, is another member of this enum —
/// a value, not a reason to design a new type around two of them.
/// </para>
/// <para>
/// <b><see cref="Bo1"/> is <c>0</c>, deliberately</b> — and that is the opposite call from
/// <see cref="DatabentoDotNet.Dbn.VersionUpgradePolicy"/>. That enum's discriminants are
/// upstream's own wire values, so they start at <c>1</c> and this port will not renumber them to
/// make a C# default line up. This enum has no wire representation at all — nothing here, and
/// nothing in upstream's, ever crosses the network as a <c>HistoricalGateway</c> — so there is no
/// upstream numbering to preserve, and starting at <c>0</c> means
/// <c>default(HistoricalGateway)</c> is <see cref="Bo1"/>: with exactly one gateway, the only
/// value the type can hold by default is also the only valid one.
/// </para>
/// </remarks>
public enum HistoricalGateway
{
    /// <summary>The default gateway, in Boston.</summary>
    Bo1 = 0,
}

/// <summary>
/// Extension methods for <see cref="HistoricalGateway"/>.
/// </summary>
public static class HistoricalGatewayExtensions
{
    /// <summary>
    /// Returns the base URL <paramref name="gateway"/> is reached at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HistoricalGateway.Bo1"/>'s literal carries no trailing slash:
    /// <c>new Uri("https://hist.databento.com").AbsoluteUri</c> is already
    /// <c>https://hist.databento.com/</c>, because <see cref="Uri"/> normalises a bare authority
    /// to a root path on its own — verified, not assumed. Writing the slash into the literal would
    /// be redundant in a way that invites someone to "tidy" it away later and read the removal as
    /// harmless, when in fact nothing would change: <see cref="Uri"/> supplies it regardless.
    /// </para>
    /// <para>
    /// This is not where a trailing slash is load-bearing. That is the client's <c>BaseUrl</c>
    /// override, which can carry a path of its own:
    /// <c>new Uri(new Uri("http://host/api"), "v0/x")</c> composes to <c>http://host/v0/x</c> —
    /// the trailing segment <c>api</c> is dropped, not appended to. Normalising that case belongs
    /// to the client that accepts the override, not to this gateway, which only ever produces a
    /// bare authority with nothing after the root slash for it to clash with.
    /// </para>
    /// </remarks>
    /// <param name="gateway">The gateway.</param>
    /// <returns>The gateway's base URL.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="gateway"/> is not a defined <see cref="HistoricalGateway"/>.
    /// </exception>
    public static Uri ToUri(this HistoricalGateway gateway) => gateway switch
    {
        HistoricalGateway.Bo1 => new Uri("https://hist.databento.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(gateway), gateway, "Undefined HistoricalGateway."),
    };
}
