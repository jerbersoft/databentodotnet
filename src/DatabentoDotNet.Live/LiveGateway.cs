using System.Net;

namespace DatabentoDotNet.Live;

/// <summary>
/// Derives the live gateway's host name from a dataset code.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>determine_gateway</c> (<c>live/protocol.rs:26-31</c>): lowercase the
/// dataset and replace every <c>.</c> with a <c>-</c>, then prepend it as a subdomain of
/// <c>lsg.databento.com</c> on port <see cref="DefaultPort"/>. <c>GLBX.MDP3</c> becomes
/// <c>glbx-mdp3.lsg.databento.com:13000</c>. Plain TCP — no TLS anywhere in the live protocol.
/// </para>
/// <para>
/// <b>The dataset is not checked against the <c>Dataset</c> enum, deliberately.</b> Upstream
/// performs no validation and says so in its own doc comment, and the reason survives the port:
/// Databento adds datasets faster than a table generated from one release of <c>publishers.rs</c>
/// tracks them, so an enum check would reject a dataset that exists in favour of one this build
/// happens to know about. Refusing to connect because our table is stale is a worse failure than
/// a DNS error naming the host that does not resolve.
/// </para>
/// <para>
/// What <em>is</em> checked is that the transformation produced something that can be a DNS
/// label at all — see <see cref="For"/>. That rejects the input a typo produces without ever
/// claiming to know which datasets exist.
/// </para>
/// </remarks>
public static class LiveGateway
{
    /// <summary>The port every live gateway listens on.</summary>
    public const int DefaultPort = 13_000;

    /// <summary>The domain the per-dataset subdomain is prepended to.</summary>
    public const string Domain = "lsg.databento.com";

    /// <summary>The longest a single DNS label may be, in bytes (RFC 1035 §2.3.4).</summary>
    private const int MaxLabelLength = 63;

    /// <summary>
    /// Returns the gateway endpoint for <paramref name="dataset"/>. Resolves no DNS — the result
    /// is a <see cref="DnsEndPoint"/> naming a host, not an address.
    /// </summary>
    /// <param name="dataset">The dataset code, in its wire spelling, such as <c>GLBX.MDP3</c>.</param>
    /// <returns>The gateway endpoint.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="dataset"/> is empty or white space, or the subdomain it produces could not
    /// be a DNS label: too long, or carrying a character other than <c>a-z</c>, <c>0-9</c> and
    /// <c>-</c>, or beginning or ending with <c>-</c>.
    /// </exception>
    public static DnsEndPoint For(string dataset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);

        var subdomain = string.Create(dataset.Length, dataset, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var character = source[i];
                destination[i] = character == '.' ? '-' : char.ToLowerInvariant(character);
            }
        });

        Validate(dataset, subdomain);
        return new DnsEndPoint($"{subdomain}.{Domain}", DefaultPort);
    }

    private static void Validate(string dataset, string subdomain)
    {
        if (subdomain.Length > MaxLabelLength)
        {
            throw new ArgumentException(
                $"'{dataset}' produces the subdomain '{subdomain}', which is {subdomain.Length} "
                + $"characters; a DNS label may be at most {MaxLabelLength}.",
                nameof(dataset));
        }

        foreach (var character in subdomain)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                throw new ArgumentException(
                    $"'{dataset}' produces the subdomain '{subdomain}', which cannot be a DNS "
                    + $"label: '{character}' is not a letter, a digit, or a hyphen.",
                    nameof(dataset));
            }
        }

        if (subdomain[0] == '-' || subdomain[^1] == '-')
        {
            throw new ArgumentException(
                $"'{dataset}' produces the subdomain '{subdomain}', which cannot be a DNS label: "
                + "a label may not begin or end with a hyphen.",
                nameof(dataset));
        }
    }
}
