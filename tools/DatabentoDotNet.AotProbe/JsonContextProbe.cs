using System.Text;
using DatabentoDotNet;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Reference;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// The source-generated JSON contexts, inside the native binary — reached the only way they can be
/// reached, by making the real clients perform real requests against
/// <see cref="LoopbackJsonServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the check the analyzers cannot make.</b> Every JSON call in the two HTTP clients
/// passes a <c>JsonTypeInfo&lt;T&gt;</c> from a source-generated context, because the reflection
/// overloads fail the build with IL2026/IL3050 — that much is already enforced at compile time. What
/// is not enforced anywhere is that the generated metadata survives ILC and the trimmer intact. A
/// context that did not would throw <see cref="NotSupportedException"/> on the first deserialize, at
/// run time, in a published binary, with nothing wrong at compile time.
/// </para>
/// <para>
/// <b>The Historical body is written here; the Reference body is the vendored capture.</b> Four
/// invented publishers are enough to prove a small model deserializes, and inventing them keeps the
/// probe from claiming to check a wire format it has no authority over — <c>MetadataClientGetTests</c>
/// owns that. The reference side uses the real 879 KB <c>corporate_actions.list_enums</c> response
/// #58 captured, because it is already vendored, because 235 groups exercise rather more of the
/// generated reader than a hand-written stub would, and because it lets the parsed result be checked
/// against the shipped <see cref="Country"/> table — two independent things agreeing, which a stub
/// could not offer.
/// </para>
/// </remarks>
internal static class JsonContextProbe
{
    private const string PublishersSlug = "/v0/metadata.list_publishers";
    private const string ListEnumsSlug = "/v0/corporate_actions.list_enums";

    /// <summary>
    /// A well-formed but fictional key. Nothing here authenticates; the clients require a
    /// syntactically valid key before they will build a request at all.
    /// </summary>
    private const string ProbeKey = "32-character-with-lots-of-filler";

    private const string Publishers = """
        [
          { "publisher_id": 1, "dataset": "GLBX.MDP3", "venue": "GLBX", "description": "CME Globex MDP 3.0" },
          { "publisher_id": 2, "dataset": "XNAS.ITCH", "venue": "XNAS", "description": "Nasdaq TotalView-ITCH" },
          { "publisher_id": 3, "dataset": "XBOS.ITCH", "venue": "XBOS", "description": "Nasdaq BX TotalView-ITCH" },
          { "publisher_id": 4, "dataset": "XPSX.ITCH", "venue": "XPSX", "description": "Nasdaq PSX TotalView-ITCH" }
        ]
        """;

    public static async Task RunAsync(ProbeReport report, CancellationToken cancellationToken)
    {
        ProbeReport.Section("historical + reference: the source-generated JSON contexts");

        var enums = await File
            .ReadAllBytesAsync(Path.Combine(DbnProbe.DataDirectory, "corporate_actions.list_enums.json"), cancellationToken)
            .ConfigureAwait(false);

        await using var server = new LoopbackJsonServer(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PublishersSlug] = Encoding.UTF8.GetBytes(Publishers),
            [ListEnumsSlug] = enums,
        });

        await HistoricalAsync(report, server.BaseUrl, cancellationToken).ConfigureAwait(false);
        await ReferenceAsync(report, server.BaseUrl, enums.Length, cancellationToken).ConfigureAwait(false);

        report.Require(server.Fault is null, $"the loopback server served both requests cleanly ({server.Fault?.Message})");
        report.RequireEqual(2, server.Served.Count, "requests reached the loopback server");
    }

    private static async Task HistoricalAsync(ProbeReport report, Uri baseUrl, CancellationToken cancellationToken)
    {
        await using var client = new HistoricalClient
        {
            ApiKey = new ApiKey(ProbeKey),
            BaseUrl = baseUrl,
        };

        var publishers = await client.Metadata.ListPublishersAsync(cancellationToken).ConfigureAwait(false);

        report.RequireEqual(4, publishers.Count, "MetadataJson read the publisher list");
        report.RequireEqual((ushort)1, publishers[0].PublisherId, "the first publisher's id");
        report.RequireEqual("GLBX.MDP3", publishers[0].Dataset, "the first publisher's dataset");
        report.RequireEqual("Nasdaq PSX TotalView-ITCH", publishers[3].Description, "the last publisher's description");

        ProbeReport.Note($"metadata.list_publishers: {publishers.Count} publishers through MetadataJson.");
    }

    private static async Task ReferenceAsync(ProbeReport report, Uri baseUrl, int bytes, CancellationToken cancellationToken)
    {
        await using var client = new ReferenceClient
        {
            ApiKey = new ApiKey(ProbeKey),
            BaseUrl = baseUrl,
        };

        var groups = await client.CorporateActions.ListEnumsAsync(cancellationToken).ConfigureAwait(false);

        report.Require(groups.Count > 0, "CorporateActionsJson read the enum groups");
        report.Require(groups.ContainsKey("CNTRY"), "the parsed groups include CNTRY");

        // The parse and the shipped table, checked against each other. Neither produced the other:
        // the table was transcribed in #50/#51 and this dictionary came off the wire through the
        // generated reader, so agreement is evidence that the reader landed the codes where the
        // model says they are — not merely that it returned something.
        var country = groups.TryGetValue("CNTRY", out var variants) ? variants : [];
        var parsed = country.Select(variant => variant.Code).Where(code => code is not null).ToHashSet(StringComparer.Ordinal);

        report.Require(parsed.Count > 0, "CNTRY carries codes");
        report.Require(parsed.SetEquals(Country.KnownCodes), "the parsed CNTRY codes are exactly Country.KnownCodes");
        report.Require(
            country.All(variant => !string.IsNullOrEmpty(variant.Description)),
            "every CNTRY variant carries a description");

        // 148 of the 235 groups list a blank code, which is the evidence behind every code carrier
        // reading a blank as "no value". A generated reader that rejected null instead would throw
        // rather than return, so reaching this line at all is half the check.
        var blanks = groups.Sum(group => group.Value.Count(variant => variant.Code is null));
        report.Require(blanks > 0, "blank codes came back as null rather than throwing");

        ProbeReport.Note(
            $"corporate_actions.list_enums: {bytes} bytes, {groups.Count} groups, "
                + $"{groups.Sum(group => group.Value.Count)} variants, {blanks} of them blank, through CorporateActionsJson.");
    }
}
