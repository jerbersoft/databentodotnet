using System.Net;
using System.Text.Json;
using DatabentoDotNet.Historical;
using DatabentoDotNet.Historical.Tests;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Tests for <see cref="CorporateActionsClient"/> — the two documentation endpoints #56 ships, the
/// five types they return, and the request shape they are supposed to have.
/// </summary>
/// <remarks>
/// <para>
/// <b>These have an oracle the rest of the reference tests do not.</b> Everywhere else in M4 the
/// mock serves bytes this repository wrote, so the harness and the client can agree with each other
/// about a misreading — the argument #57 exists to settle. Not here: <c>Data/</c> holds the live
/// API's own responses to these two endpoints, captured verbatim (#58), and the tests below serve
/// them back and check what the client made of them against <see cref="ReferenceEnumFixture"/>,
/// which reads the same bytes with <see cref="JsonDocument"/> and none of this library's models.
/// Two independent readers over one set of production bytes is a real cross-check.
/// </para>
/// <para>
/// The hand-written fixtures are still needed for what the capture does not contain: seven of
/// <see cref="EventDoc"/>'s nine properties were populated on all 60 of its events, so the absent
/// and explicitly-null cases have no natural example in it.
/// </para>
/// </remarks>
public class CorporateActionsClientTests
{
    private const string ListEvents = "corporate_actions.list_events";
    private const string ListEnums = "corporate_actions.list_enums";

    /// <summary>
    /// One event with all nine properties populated, both <see cref="EventDocCalendarDates.Alias"/>
    /// cases, all three field groups, and both <see cref="EventDocSubType.Code"/> cases.
    /// </summary>
    private const string FullDoc = """
        {"AGM":{"calendar_dates":[{"alias":"meeting_date","name":"event_date"},{"alias":null,"name":"ex_date"}],"category":"proposals","code":"AGM","description":"The issuer's annual general meeting.","fields":[{"description":"The meeting agenda.","group":"event_info","name":"agenda"},{"description":"The record date.","group":"date_info","name":"record_date"},{"description":"The vote rate.","group":"rate_info","name":"vote_rate"}],"level":"security","name":"Annual General Meeting","participation":"mandatory","subtypes":[{"code":"AGMEGM","description":"Combined AGM and EGM."},{"code":null,"description":"No subtype given."}]}}
        """;

    /// <summary>The same event reduced to the two properties upstream types without an Option.</summary>
    private const string SparseDoc = """
        {"AGM":{"level":"security","name":"Annual General Meeting"}}
        """;

    /// <summary>
    /// The two required properties, and all seven optional ones present and explicitly
    /// <c>null</c>. Distinct from <see cref="SparseDoc"/>: a model that rejected a null token would
    /// read that one and fail this.
    /// </summary>
    private const string NulledDoc = """
        {"AGM":{"calendar_dates":null,"category":null,"code":null,"description":null,"fields":null,"level":"security","name":"Annual General Meeting","participation":null,"subtypes":null}}
        """;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly string[] EventDocProperties =
    [
        "CalendarDates", "Category", "Code", "Description", "Fields",
        "Level", "Name", "Participation", "Subtypes",
    ];

    private static readonly string[] CalendarDatesProperties = ["Alias", "Name"];

    private static readonly string[] FieldProperties = ["Description", "Group", "Name"];

    private static readonly string[] CodeAndDescription = ["Code", "Description"];


    /* ------------------------------------------------------------------ *
     *  Request shape: two bare GETs                                       *
     * ------------------------------------------------------------------ */

    /// <summary>
    /// Each endpoint posts nothing and gets its own versioned path. The slug is spelled out here
    /// rather than read from the client, so a renamed constant fails rather than follows.
    /// </summary>
    /// <param name="slug">The endpoint under test.</param>
    [Theory]
    [InlineData(ListEvents)]
    [InlineData(ListEnums)]
    public async Task BothEndpointsGetTheirVersionedSlug(string slug)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(slug, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        await CallAsync(client, slug);

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal("/v0/" + slug, recorded.Path);
    }

    /// <summary>
    /// Neither request carries a body or a query string. Upstream sends both as bare <c>GET</c>s
    /// (<c>corporate.rs:75-79</c>, <c>:91-95</c>) and there is no parameter type for either, so the
    /// only way this could fail is a transport that appended something of its own.
    /// </summary>
    /// <param name="slug">The endpoint under test.</param>
    [Theory]
    [InlineData(ListEvents)]
    [InlineData(ListEnums)]
    public async Task BothEndpointsSendNoBodyAndNoQueryString(string slug)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(slug, MockHistoricalResponse.Json("{}"));

        await using var client = ClientFor(gateway);
        await CallAsync(client, slug);

        gateway.ThrowIfRejected();

        var recorded = Assert.Single(gateway.Requests);
        Assert.Equal(string.Empty, recorded.RawQuery);
        Assert.True(recorded.Body.IsEmpty);
    }

    /* ------------------------------------------------------------------ *
     *  The vendored production responses, against an independent reader   *
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The captured <c>list_events</c> response, served back and read by the client, agrees with
    /// <see cref="ReferenceEnumFixture"/> on every event and on every field that fixture models.
    /// </summary>
    /// <remarks>
    /// The strongest test in this file: the bytes are the live API's, and the two readers share no
    /// code. A misreading in <see cref="EventDoc"/> that a hand-written fixture would have been
    /// written to match cannot survive here.
    /// </remarks>
    [Fact]
    public async Task ListEventsAsync_ReadsTheVendoredResponseAsAnIndependentReaderDoes()
    {
        var docs = await ServeAsync(ListEvents, r => r.CorporateActions.ListEventsAsync(Cancel));
        var oracle = ReferenceEnumFixture.Instance;

        Assert.Equal(oracle.Events.Count, docs.Count);
        Assert.Equal(oracle.Events.Keys.Order(), docs.Keys.Order());

        foreach (var (code, expected) in oracle.Events)
        {
            var actual = docs[code];

            Assert.Equal(expected.Category, actual.Category.Code);
            Assert.Equal(expected.Level, actual.Level.Code);
            Assert.Equal(
                expected.FieldGroups.Order(),
                (actual.Fields ?? []).Select(f => f.Group.Code!).Distinct().Order());
            Assert.Equal(
                expected.SubtypeCodes.Order(),
                (actual.Subtypes ?? []).Where(s => s.Code.HasValue).Select(s => s.Code.Code!)
                    .Distinct().Order());
        }
    }

    /// <summary>
    /// The captured <c>list_enums</c> response, read the same way — 235 groups and 13,123 variants,
    /// every code and description matching the independent reader, nulls included.
    /// </summary>
    [Fact]
    public async Task ListEnumsAsync_ReadsTheVendoredResponseAsAnIndependentReaderDoes()
    {
        var groups = await ServeAsync(ListEnums, r => r.CorporateActions.ListEnumsAsync(Cancel));
        var oracle = ReferenceEnumFixture.Instance;

        Assert.Equal(oracle.Groups.Count, groups.Count);
        Assert.Equal(oracle.Groups.Keys.Order(), groups.Keys.Order());

        foreach (var (name, expected) in oracle.Groups)
        {
            var actual = groups[name];
            Assert.Equal(expected.Count, actual.Count);

            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Code, actual[i].Code);
                Assert.Equal(expected[i].Description, actual[i].Description);
            }
        }
    }

    /// <summary>
    /// Every <see cref="EventDocField.Group"/> in the captured response is one of
    /// <c>CorporateAction</c>'s three open maps, and every one of the three is used.
    /// </summary>
    /// <remarks>
    /// #56's definition of done, and #55's reason for caring: a fourth group would mean the 104-field
    /// model is missing a column, and this is where that surfaces. Asserted by name so a failure
    /// reports the group rather than a count.
    /// </remarks>
    [Fact]
    public async Task EveryFieldGroupIsOneOfCorporateActionsThreeOpenMaps()
    {
        var docs = await ServeAsync(ListEvents, r => r.CorporateActions.ListEventsAsync(Cancel));

        var seen = docs.Values
            .SelectMany(d => d.Fields ?? [])
            .Select(f => f.Group)
            .ToList();

        Assert.NotEmpty(seen);
        Assert.Empty(seen.Where(g => !g.IsKnown).Select(g => g.Code).Distinct());
        Assert.Equal(
            new[]
            {
                FieldGroup.DateInfo.Code, FieldGroup.EventInfo.Code, FieldGroup.RateInfo.Code,
            }.Order(),
            seen.Select(g => g.Code!).Distinct().Order());
    }

    /// <summary>
    /// Every document in the captured response repeats its own key in <see cref="EventDoc.Code"/>.
    /// </summary>
    /// <remarks>
    /// True of all 60, and deliberately not relied on anywhere:
    /// <see cref="CorporateActionsClient.ListEventsAsync"/> keys by the string the server filed the
    /// document under. The assertion is here so that if it ever stops being true, it is a finding
    /// rather than a surprise.
    /// </remarks>
    [Fact]
    public async Task EveryDocumentRepeatsItsOwnKeyInItsCode()
    {
        var docs = await ServeAsync(ListEvents, r => r.CorporateActions.ListEventsAsync(Cancel));

        Assert.All(docs, entry => Assert.Equal(entry.Key, entry.Value.Code.Code));
    }

    /// <summary>
    /// <see cref="EventDoc.Participation"/> and <see cref="MandVolu"/> describe the same idea and
    /// share not one code, which is why the first is a <see cref="string"/>.
    /// </summary>
    /// <remarks>
    /// The #45 mistake in miniature is unifying two vocabularies that agree in meaning and disagree
    /// on the wire, and this is the assertion that says which of the two this is. Both sides come
    /// from the captured responses: <c>participation</c> from <c>list_events</c>, <c>MANDVOLU</c>
    /// from <c>list_enums</c>.
    /// </remarks>
    [Fact]
    public async Task ParticipationAndMandVoluShareNoCode()
    {
        var docs = await ServeAsync(ListEvents, r => r.CorporateActions.ListEventsAsync(Cancel));

        var participation = docs.Values
            .Select(d => d.Participation)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToHashSet(StringComparer.Ordinal);

        var mandVolu = ReferenceEnumFixture.Instance.CodesIn("MANDVOLU");

        Assert.NotEmpty(participation);
        Assert.NotEmpty(mandVolu);
        Assert.Empty(participation.Intersect(mandVolu, StringComparer.Ordinal));
    }

    /* ------------------------------------------------------------------ *
     *  Optionality: seven of nine, present, absent, and null              *
     * ------------------------------------------------------------------ */

    /// <summary>A document with all nine properties populated reads all nine.</summary>
    [Fact]
    public async Task ListEventsAsync_ReadsAFullyPopulatedDocument()
    {
        var doc = await SingleDocAsync(FullDoc);

        Assert.Equal("Annual General Meeting", doc.Name);
        Assert.Equal("The issuer's annual general meeting.", doc.Description);
        Assert.Equal("mandatory", doc.Participation);
        Assert.Equal(EventCategory.Proposals, doc.Category);
        Assert.Equal(EventLevel.Security, doc.Level);
        Assert.Equal("AGM", doc.Code.Code);

        var dates =
            Assert.IsAssignableFrom<IReadOnlyList<EventDocCalendarDates>>(doc.CalendarDates);
        Assert.Equal(2, dates.Count);
        Assert.Equal("meeting_date", dates[0].Alias);
        Assert.Equal("event_date", dates[0].Name);
        Assert.Null(dates[1].Alias);
        Assert.Equal("ex_date", dates[1].Name);

        var fields = Assert.IsAssignableFrom<IReadOnlyList<EventDocField>>(doc.Fields);
        Assert.Equal(3, fields.Count);
        Assert.Equal(FieldGroup.EventInfo, fields[0].Group);
        Assert.Equal("agenda", fields[0].Name);
        Assert.Equal("The meeting agenda.", fields[0].Description);
        Assert.Equal(FieldGroup.DateInfo, fields[1].Group);
        Assert.Equal(FieldGroup.RateInfo, fields[2].Group);

        var subtypes = Assert.IsAssignableFrom<IReadOnlyList<EventDocSubType>>(doc.Subtypes);
        Assert.Equal(2, subtypes.Count);
        Assert.Equal("AGMEGM", subtypes[0].Code.Code);
        Assert.Equal("Combined AGM and EGM.", subtypes[0].Description);
        Assert.False(subtypes[1].Code.HasValue);
    }

    /// <summary>
    /// A document carrying only the two properties upstream types without an <c>Option</c> reads,
    /// and the other seven come back absent.
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_ReadsADocumentWithEveryOptionalPropertyAbsent()
    {
        AssertEveryOptionalPropertyIsAbsent(await SingleDocAsync(SparseDoc));
    }

    /// <summary>
    /// The same seven, present and explicitly <c>null</c>. Distinct from the absent case: a model
    /// that rejected a null token would pass the test above and fail here.
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_ReadsADocumentWithEveryOptionalPropertyExplicitlyNull()
    {
        AssertEveryOptionalPropertyIsAbsent(await SingleDocAsync(NulledDoc));
    }

    /// <summary>
    /// <see cref="EventDoc.Level"/> is the one code carrier upstream types without an
    /// <c>Option</c>, so a document without it fails to read rather than arriving with an absent
    /// one.
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_RefusesADocumentWithNoLevel()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
            SingleDocAsync("""{"AGM":{"name":"Annual General Meeting"}}"""));
    }

    /// <summary><see cref="EventDoc.Name"/> is required for the same reason.</summary>
    [Fact]
    public async Task ListEventsAsync_RefusesADocumentWithNoName()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
            SingleDocAsync("""{"AGM":{"level":"security"}}"""));
    }

    /// <summary>
    /// <see cref="EventEnumVariant.Description"/> is required, and its
    /// <see cref="EventEnumVariant.Code"/> is not — the asymmetry upstream declares, and the one
    /// the captured response bears out.
    /// </summary>
    [Fact]
    public async Task ListEnumsAsync_RefusesAVariantWithNoDescription()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
            GroupsAsync("""{"MANDVOLU":[{"code":"M"}]}"""));
    }

    /* ------------------------------------------------------------------ *
     *  Open codes: the point of the ten carriers                          *
     * ------------------------------------------------------------------ */

    /// <summary>
    /// A document using four codes this library has never seen keeps all four, verbatim, and
    /// reports each as unknown rather than throwing.
    /// </summary>
    /// <remarks>
    /// The codes below were checked against both the shipped tables and the captured
    /// <c>list_enums</c> response before being used, because a "made-up" code that turns out to be
    /// real makes this test assert the opposite of what it says.
    /// </remarks>
    [Fact]
    public async Task ListEventsAsync_KeepsCodesThisLibraryDoesNotKnow()
    {
        const string Unknown = """
            {"XQZ":{"category":"quantum_entanglement","code":"XQZ","fields":[{"description":"d","group":"quark_info","name":"spin"}],"level":"galaxy","name":"Made up","subtypes":[{"code":"ZZTOP","description":"d"}]}}
            """;

        var doc = await SingleDocAsync(Unknown, key: "XQZ");

        Assert.Equal("quantum_entanglement", doc.Category.Code);
        Assert.False(doc.Category.IsKnown);
        Assert.Equal("galaxy", doc.Level.Code);
        Assert.False(doc.Level.IsKnown);
        Assert.Equal("XQZ", doc.Code.Code);
        Assert.False(doc.Code.IsKnown);

        var field = Assert.Single(doc.Fields!);
        Assert.Equal("quark_info", field.Group.Code);
        Assert.False(field.Group.IsKnown);

        var subtype = Assert.Single(doc.Subtypes!);
        Assert.Equal("ZZTOP", subtype.Code.Code);
        Assert.False(subtype.Code.IsKnown);
    }

    /// <summary>
    /// An event code this library does not know still arrives under its own key. Keying by a parsed
    /// <see cref="Event"/> is what would lose it, which is why neither this client nor upstream
    /// does that.
    /// </summary>
    [Fact]
    public async Task ListEventsAsync_FilesAnUnknownEventUnderItsOwnKey()
    {
        const string Unknown = """
            {"XQZ":{"level":"security","name":"Made up"},"AGM":{"level":"security","name":"Annual General Meeting"}}
            """;

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json(Unknown));

        await using var client = ClientFor(gateway);
        var docs = await client.CorporateActions.ListEventsAsync(Cancel);

        gateway.ThrowIfRejected();
        Assert.Equal(2, docs.Count);
        Assert.True(docs.ContainsKey("XQZ"));
        Assert.Equal("Made up", docs["XQZ"].Name);
    }

    /// <summary>A subtype with a <c>null</c> code reads as no value rather than as an error.</summary>
    [Fact]
    public async Task ListEventsAsync_ReadsABlankSubtypeCodeAsNoValue()
    {
        var doc = await SingleDocAsync("""
            {"AGM":{"level":"security","name":"n","subtypes":[{"code":null,"description":"d"}]}}
            """);

        var subtype = Assert.Single(doc.Subtypes!);
        Assert.False(subtype.Code.HasValue);
        Assert.Null(subtype.Code.Code);
    }

    /// <summary>
    /// A group's blank entry reads as a <see langword="null"/> code. 148 of the 235 groups the live
    /// endpoint returned carry one, and they are the evidence behind every code carrier treating a
    /// blank as an absence.
    /// </summary>
    [Fact]
    public async Task ListEnumsAsync_ReadsAGroupsBlankEntryAsANullCode()
    {
        var groups = await GroupsAsync("""
            {"FRACCD":[{"code":null,"description":""},{"code":"D","description":"Round down"}]}
            """);

        var variants = groups["FRACCD"];
        Assert.Equal(2, variants.Count);
        Assert.Null(variants[0].Code);
        Assert.Equal(string.Empty, variants[0].Description);
        Assert.Equal("D", variants[1].Code);
    }

    /// <summary>
    /// A group name this library models no type for keeps its entry. 235 groups arrive and ten
    /// types read from them, so this is the ordinary case rather than the exceptional one.
    /// </summary>
    [Fact]
    public async Task ListEnumsAsync_KeepsAGroupThisLibraryModelsNoTypeFor()
    {
        var groups = await GroupsAsync("""
            {"NOTATYPEHERE":[{"code":"Q","description":"d"}]}
            """);

        Assert.Equal("Q", Assert.Single(groups["NOTATYPEHERE"]).Code);
    }

    /// <summary>
    /// The dictionary keys are the server's own, compared ordinally. <c>AGM</c> is not <c>agm</c>,
    /// as upstream's <c>HashMap&lt;String, _&gt;</c> is not.
    /// </summary>
    [Fact]
    public async Task TheKeysAreTheServersOwnAndAreCaseSensitive()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json(SparseDoc));

        await using var client = ClientFor(gateway);
        var docs = await client.CorporateActions.ListEventsAsync(Cancel);

        gateway.ThrowIfRejected();
        Assert.True(docs.ContainsKey("AGM"));
        Assert.False(docs.ContainsKey("agm"));
    }

    /* ------------------------------------------------------------------ *
     *  Plumbing                                                           *
     * ------------------------------------------------------------------ */

    /// <summary>The subclient is built once and cached, like its two siblings.</summary>
    [Fact]
    public async Task CorporateActions_IsBuiltOnceAndCached()
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        await using var client = ClientFor(gateway);

        Assert.Same(client.CorporateActions, client.CorporateActions);
        Assert.NotSame(client.CorporateActions, (object)client.AdjustmentFactors);
        Assert.NotSame(client.CorporateActions, (object)client.SecurityMaster);
    }

    /// <summary>
    /// A disposed client is refused on the returned task rather than at the call, and that is not
    /// the inconsistency it looks like: these two return a <see cref="Task{TResult}"/>, which
    /// carries its own exception to the first <c>await</c>. The three <c>get_range</c> endpoints
    /// return an <see cref="IAsyncEnumerable{T}"/>, which would defer the same throw to the first
    /// <c>MoveNextAsync</c> — or lose it entirely for a caller who never enumerates — which is why
    /// those validate eagerly and these do not need to.
    /// </summary>
    /// <param name="slug">The endpoint under test.</param>
    [Theory]
    [InlineData(ListEvents)]
    [InlineData(ListEnums)]
    public async Task BothEndpointsRefuseADisposedClient(string slug)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        var client = ClientFor(gateway);
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => CallAsync(client, slug));

        Assert.Empty(gateway.Requests);
    }

    /// <summary>
    /// A non-success status reaches the caller as a <see cref="DatabentoApiException"/> rather than
    /// as an empty document. A <c>403</c> is the realistic one: reference data is a separate
    /// Databento product from historical data.
    /// </summary>
    /// <param name="slug">The endpoint under test.</param>
    [Theory]
    [InlineData(ListEvents)]
    [InlineData(ListEnums)]
    public async Task BothEndpointsSurfaceANonSuccessStatus(string slug)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(
            slug, MockHistoricalResponse.Json("""{"detail":"no reference entitlement"}""", 403));

        await using var client = ClientFor(gateway);
        var thrown = await Assert.ThrowsAsync<DatabentoApiException>(() => CallAsync(client, slug));

        gateway.ThrowIfRejected();
        Assert.Equal(HttpStatusCode.Forbidden, thrown.StatusCode);
    }

    /// <summary>
    /// The JSON literal <c>null</c> is a decode failure rather than an empty document, which is the
    /// call <see cref="HistoricalClient.ReadJsonAsync{T}"/> makes for every endpoint that reads
    /// through it.
    /// </summary>
    /// <param name="slug">The endpoint under test.</param>
    [Theory]
    [InlineData(ListEvents)]
    [InlineData(ListEnums)]
    public async Task BothEndpointsRefuseALiteralNullBody(string slug)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(slug, MockHistoricalResponse.Json("null"));

        await using var client = ClientFor(gateway);
        await Assert.ThrowsAsync<JsonException>(() => CallAsync(client, slug));
    }

    /* ------------------------------------------------------------------ *
     *  Shape guards                                                       *
     * ------------------------------------------------------------------ */

    /// <summary>
    /// The five types carry exactly the properties upstream declares. A property added without a
    /// test, or one lost in a rename, fails here rather than silently stopping matching a wire
    /// field — the same guard <c>SecurityMaster</c> carries for its fifty.
    /// </summary>
    [Fact]
    public void TheFiveTypesCarryExactlyTheirUpstreamProperties()
    {
        Assert.Equal(EventDocProperties.Order(), PropertyNames<EventDoc>());
        Assert.Equal(CalendarDatesProperties.Order(), PropertyNames<EventDocCalendarDates>());
        Assert.Equal(CodeAndDescription.Order(), PropertyNames<EventDocSubType>());
        Assert.Equal(FieldProperties.Order(), PropertyNames<EventDocField>());
        Assert.Equal(CodeAndDescription.Order(), PropertyNames<EventEnumVariant>());
    }

    /* ------------------------------------------------------------------ *
     *  Helpers                                                            *
     * ------------------------------------------------------------------ */

    private static IOrderedEnumerable<string> PropertyNames<T>() =>
        typeof(T).GetProperties()
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Order();

    private static void AssertEveryOptionalPropertyIsAbsent(EventDoc doc)
    {
        Assert.Null(doc.CalendarDates);
        Assert.False(doc.Category.HasValue);
        Assert.False(doc.Code.HasValue);
        Assert.Null(doc.Description);
        Assert.Null(doc.Fields);
        Assert.Null(doc.Participation);
        Assert.Null(doc.Subtypes);

        // The two upstream types without an Option, so that a model which had quietly made
        // everything optional would fail this rather than pass it.
        Assert.Equal(EventLevel.Security, doc.Level);
        Assert.Equal("Annual General Meeting", doc.Name);
    }

    private static Task CallAsync(ReferenceClient client, string slug) => slug switch
    {
        ListEvents => client.CorporateActions.ListEventsAsync(Cancel),
        ListEnums => client.CorporateActions.ListEnumsAsync(Cancel),
        _ => throw new ArgumentOutOfRangeException(nameof(slug), slug, null),
    };

    private static async Task<EventDoc> SingleDocAsync(string body, string key = "AGM")
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEvents, MockHistoricalResponse.Json(body));

        await using var client = ClientFor(gateway);
        var docs = await client.CorporateActions.ListEventsAsync(Cancel);

        gateway.ThrowIfRejected();
        return docs[key];
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>>>
        GroupsAsync(string body)
    {
        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(ListEnums, MockHistoricalResponse.Json(body));

        await using var client = ClientFor(gateway);
        var groups = await client.CorporateActions.ListEnumsAsync(Cancel);

        gateway.ThrowIfRejected();
        return groups;
    }

    /// <summary>
    /// Serves the vendored response for <paramref name="slug"/> back through the gateway and reads
    /// it with the client.
    /// </summary>
    /// <typeparam name="T">What the endpoint returns.</typeparam>
    /// <param name="slug">The endpoint, which also names the file.</param>
    /// <param name="call">The client call to make.</param>
    /// <returns>What the client read.</returns>
    private static async Task<T> ServeAsync<T>(string slug, Func<ReferenceClient, Task<T>> call)
    {
        var body = await File.ReadAllTextAsync(
            Path.Combine(ReferenceEnumFixture.Directory, slug + ".json"), Cancel);

        await using var gateway = await MockHistoricalGateway.StartAsync(Cancel);
        gateway.Get(slug, MockHistoricalResponse.Json(body));

        await using var client = ClientFor(gateway);
        var read = await call(client);

        gateway.ThrowIfRejected();
        return read;
    }

    private static ReferenceClient ClientFor(MockHistoricalGateway gateway) => new()
    {
        ApiKey = new ApiKey(MockHistoricalGateway.TestApiKey),
        BaseUrl = gateway.BaseUrl,
    };
}
