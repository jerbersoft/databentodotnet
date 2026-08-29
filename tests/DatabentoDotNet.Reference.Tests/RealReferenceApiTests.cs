using System.Net;
using DatabentoDotNet.Dbn;
using DatabentoDotNet.Historical;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Opt-in tests against the <b>real</b> Databento reference API, covering the endpoints that cost
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> <c>MockHistoricalGateway</c> serves the bytes it was handed, so a
/// misreading of a reference response shape would sit in both the harness and the client and the
/// two would agree with each other. CLAUDE.md states the consequence: the mock cannot confirm what
/// it shares an author with. That argument closed M2 and M3, and M3's first real call immediately
/// found #45 — an inclusive <c>end_date</c> the mock had been agreeing with for as long as both
/// existed.
/// </para>
/// <para>
/// <b>What this class asks that nothing else can: is the vendored fixture still what the server
/// says?</b> #58 captured <c>list_enums</c> and <c>list_events</c> off the wire, and #50 and #51
/// transcribe the ten shipped code tables from them — so "our tables match the server" already has
/// an offline baseline, and a mismatch there is a build failure rather than something this issue
/// discovers late. What that baseline cannot notice is the fixture ageing. The probe that produced
/// it found <see cref="SecurityType"/> at 30 of 64, <see cref="Frequency"/> at 14 of 16 and
/// <see cref="Event"/> stale in both directions, which is the finding that this dictionary
/// <em>moves</em>. So the live check here is fixture-versus-server, and the offline tests are
/// tables-versus-fixture: between them every code is named on one side or the other, and each test
/// fails for exactly one reason.
/// </para>
/// <para>
/// <b>Nothing here costs money, and that is measured rather than reasoned.</b> #57 asks for the
/// free classification to be established rather than assumed, warning that "these are
/// documentation <c>GET</c>s" is a prior and not a probe. The probe answered something stronger
/// than a price: <b>both endpoints are unauthenticated</b>, answering in full with no
/// <c>Authorization</c> header at all, while <c>metadata.list_datasets</c> and
/// <c>corporate_actions.get_range</c> refuse the same request. A call that carries no account
/// cannot be billed to one. See
/// <see cref="TheDocumentationEndpoints_AnswerWithoutAValidCredential"/>, which is where that was
/// measured and is the load-bearing evidence for this whole class. The endpoints that return rows
/// authenticate, are billed, and live in <see cref="RealReferenceRequestTests"/> behind
/// <see cref="ReferenceCredentials.RequestVariable"/> — a separate file so this class's promise is
/// checkable by reading the file list.
/// </para>
/// <para>
/// <b>Nothing new belongs in <em>this</em> class past that line.</b> A test here that quietly grows
/// a data request is a test that quietly grows a bill, and it would take the class's "free to run"
/// guarantee with it. The same rule <c>RealHistoricalApiTests</c> and <c>RealGatewaySmokeTests</c>
/// carry.
/// </para>
/// <para>
/// They skip rather than fail when no key is configured, and CI filters
/// <c>Category=Reference</c> out by name as well. See <see cref="ReferenceCredentials"/>.
/// </para>
/// </remarks>
[Trait("Category", "Reference")]
public class RealReferenceApiTests
{
    /// <summary>Gate for every <c>SkipUnless</c> in this class: a key, and nothing more.</summary>
    public static bool IsConfigured => ReferenceCredentials.IsConfigured;

    /// <summary>
    /// A syntactically valid key that is not a real one, so <see cref="ApiKey"/> accepts it and the
    /// API is the thing that decides what to do with it. Derived from <see cref="ApiKey.Length"/>
    /// rather than typed out, for the reason <c>RealGatewaySmokeTests</c> gives.
    /// </summary>
    /// <remarks>
    /// What the API decides here is <em>to answer anyway</em>, which is the finding
    /// <see cref="TheDocumentationEndpoints_AnswerWithoutAValidCredential"/> records. The same
    /// value against <c>get_range</c> is refused with <c>401</c>.
    /// </remarks>
    private static readonly string NotARealKey = "db-" + new string('0', ApiKey.Length - 3);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ReferenceEnumFixture Fixture => ReferenceEnumFixture.Instance;

    private static ReferenceClient Client() => new() { ApiKey = ReferenceCredentials.ApiKey };

    // ----------------------------------------------------------------------------------------
    // list_enums — the corporate-actions data dictionary, against the copy of it #58 vendored.
    // ----------------------------------------------------------------------------------------

    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEnums_ReportsTheSameGroupsTheVendoredFixtureCarries()
    {
        var live = await LiveEnums();

        AssertSameNames(
            "list_enums groups",
            live.Keys.ToHashSet(StringComparer.Ordinal),
            Fixture.Groups.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every group's codes, compared one group at a time so a failure names the group as well as
    /// the codes.
    /// </summary>
    /// <remarks>
    /// A single set comparison across all 235 groups would report a code without saying which
    /// dictionary it moved in, and the whole point of this test is to hand the next person the
    /// re-capture they need to make. Groups the two sides do not share are left to the test above,
    /// which is the one that reports them.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEnums_ReportsTheSameCodesInEveryGroupTheVendoredFixtureCarries()
    {
        var live = await LiveEnums();
        var drifted = new List<string>();

        foreach (var (group, variants) in Fixture.Groups)
        {
            if (!live.TryGetValue(group, out var serverVariants))
            {
                continue;
            }

            var fixtureCodes = variants.Where(v => v.Code is not null).Select(v => v.Code!)
                .ToHashSet(StringComparer.Ordinal);
            var serverCodes = serverVariants.Where(v => v.Code is not null).Select(v => v.Code!)
                .ToHashSet(StringComparer.Ordinal);

            if (Describe(group, serverCodes, fixtureCodes) is { } drift)
            {
                drifted.Add(drift);
            }
        }

        Assert.True(
            drifted.Count == 0,
            $"{drifted.Count} enum group(s) have moved since the fixture was captured. Re-capture "
            + $"Data/{ReferenceEnumFixture.EnumsFileName} and re-run the offline table tests, "
            + $"which is where the shipped tables get corrected:{Environment.NewLine}"
            + string.Join(Environment.NewLine, drifted));
    }

    /// <summary>
    /// Which groups allow a blank, which is the evidence behind every nullable model field.
    /// </summary>
    /// <remarks>
    /// A group gaining a blank entry means a field this library models as non-nullable can now
    /// legitimately arrive empty — <see cref="CorporateAction.PaymentType"/> and
    /// <see cref="CorporateAction.Fraction"/> are nullable for exactly this reason, and they are
    /// the only two that are. Losing one is harmless; gaining one is a model bug in waiting, so
    /// both directions are reported.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEnums_AllowsABlankInTheSameGroupsTheVendoredFixtureDoes()
    {
        var live = await LiveEnums();

        var liveBlanks = live
            .Where(g => g.Value.Any(v => v.Code is null))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var fixtureBlanks = Fixture.Groups.Keys
            .Where(Fixture.HasBlank)
            .ToHashSet(StringComparer.Ordinal);

        AssertSameNames("groups allowing a blank code", liveBlanks, fixtureBlanks);
    }

    /// <summary>
    /// The shipped tables against the live dictionary directly, rather than through the fixture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the assertion #57 asks for in its own words</b> — every code the server reports
    /// is a known member, and the ones that are not are named. It is implied by the two tests above
    /// plus <see cref="ReferenceCodeTableTests"/>, and it is written out anyway: a reader looking
    /// for "do we know every code Databento has" should find it asserted rather than have to
    /// compose it from two other files, and a chain of two implications is one link longer than an
    /// error message should be.
    /// </para>
    /// <para>
    /// The group-to-type mapping is a second copy of <see cref="ReferenceCodeTableTests"/>'. That
    /// is deliberate and cheap: seven rows, and a divergence fails one of the two loudly rather
    /// than passing quietly. What the two tests mean is genuinely different — one asks whether the
    /// tables are right, the other whether the fixture is current — and folding them would produce
    /// a test that cannot say which.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEnums_ReportsNoCodeTheShippedTablesDoNotKnow()
    {
        var live = await LiveEnums();

        AssertKnown<Country>(live, "CNTRY");
        AssertKnown<Currency>(live, "CUREN");
        AssertKnown<Event>(live, "EVENT");
        AssertKnown<EventSubType>(live, "EVENTSUBTYPE");
        AssertKnown<SecurityType>(live, "SECTYPE");
        AssertKnown<Frequency>(live, "FREQ");
        AssertKnown<OutturnStyle>(live, "OUTTURNSTYLE");
    }

    // ----------------------------------------------------------------------------------------
    // list_events — the only authority for EventCategory, EventLevel and FieldGroup.
    // ----------------------------------------------------------------------------------------

    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEvents_DocumentsTheSameEventsTheVendoredFixtureCarries()
    {
        var live = await LiveEvents();

        AssertSameNames(
            "list_events documented events",
            live.Keys.ToHashSet(StringComparer.Ordinal),
            Fixture.Events.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every <c>EventCategory</c>, <c>EventLevel</c> and <c>EventSubType</c> the live response
    /// carries is one this library knows.
    /// </summary>
    /// <remarks>
    /// <b>These three are typed on the model, so the parse is the test.</b>
    /// <see cref="EventDoc.Category"/> is an <see cref="EventCategory"/> and not a string, which
    /// means reading a live response already ran every code through the carrier; what is left to
    /// assert is that none of them landed as unknown. That is why this class needs no
    /// group-to-type mapping for the three <c>list_enums</c> has no group for.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEvents_CarriesNoCategoryLevelOrSubtypeTheShippedTablesDoNotKnow()
    {
        var live = await LiveEvents();
        var unknown = new List<string>();

        foreach (var (code, doc) in live)
        {
            // Category is optional on the wire — `default` means the server sent none, which is
            // an absence rather than an unrecognised code. Level is required, so a `default` there
            // would already have thrown in the converter.
            if (doc.Category is { HasValue: true, IsKnown: false })
            {
                unknown.Add($"{code}: category '{doc.Category.Code}'");
            }

            if (!doc.Level.IsKnown)
            {
                unknown.Add($"{code}: level '{doc.Level.Code}'");
            }

            foreach (var subtype in doc.Subtypes ?? [])
            {
                if (subtype.Code is { HasValue: true, IsKnown: false })
                {
                    unknown.Add($"{code}: subtype '{subtype.Code.Code}'");
                }
            }
        }

        Assert.True(
            unknown.Count == 0,
            $"list_events carries {unknown.Count} code(s) the shipped tables do not know. They "
            + "arrived in the open carriers rather than throwing, so nothing is broken — but the "
            + $"tables are behind the server:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unknown));
    }

    /// <summary>
    /// Every <c>group</c> the live <c>fields</c> carry is one of <see cref="CorporateAction"/>'s
    /// three open maps.
    /// </summary>
    /// <remarks>
    /// <b>A fourth group means the model is missing a column, and this is where that surfaces.</b>
    /// <see cref="CorporateAction"/> holds its variable columns in exactly three dictionaries —
    /// <see cref="CorporateAction.DateInfo"/>, <see cref="CorporateAction.RateInfo"/> and
    /// <see cref="CorporateAction.EventInfo"/> — because those are the three groups every
    /// documented field has ever declared. Nothing in the response type enforces that, and a fourth
    /// would arrive as a <see cref="FieldGroup"/> nobody reads: the rows carrying it would parse
    /// green and the data would simply not be there.
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task ListEvents_DeclaresNoFieldGroupOutsideCorporateActionsThreeMaps()
    {
        var live = await LiveEvents();

        // Compared as FieldGroup values rather than as their codes: the carrier is a record
        // struct, so equality is the code's, and this way the set cannot be built from a spelling
        // that differs from the one the model actually reads.
        var modelled = new HashSet<FieldGroup>
        {
            FieldGroup.DateInfo,
            FieldGroup.RateInfo,
            FieldGroup.EventInfo,
        };

        var surplus = live
            .SelectMany(e => (IEnumerable<EventDocField>)(e.Value.Fields ?? []), (e, f) => (e.Key, f.Group))
            .Where(x => !modelled.Contains(x.Group))
            .Select(x => $"{x.Key}: '{x.Group.Code}'")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            surplus.Count == 0,
            $"list_events declares {surplus.Count} field(s) in a group CorporateAction has no map "
            + "for, so those columns would parse green and go nowhere. CorporateAction needs a "
            + $"fourth dictionary:{Environment.NewLine}" + string.Join(Environment.NewLine, surplus));
    }

    // ----------------------------------------------------------------------------------------
    // Credentials — and the finding that made this section's first draft wrong.
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The two documentation endpoints answer a key the API does not know, and answer no key at
    /// all, with the full response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test asserted a <c>401</c> until the first run, and the first run said otherwise.</b>
    /// <c>corporate_actions.list_enums</c> and <c>corporate_actions.list_events</c> are
    /// unauthenticated: measured on 2026-08-29, both answered <c>200</c> with their complete bodies
    /// — 879,114 and 71,489 bytes — for a syntactically valid key that is not a real one <em>and</em>
    /// for a request carrying no <c>Authorization</c> header at all. The control ran in the same
    /// minute: <c>metadata.list_datasets</c> refused the same fake key with <c>401</c>, and so does
    /// <c>corporate_actions.get_range</c>, so this is a property of these two endpoints rather than
    /// of the key, the host or the transport.
    /// </para>
    /// <para>
    /// <b>It is the strongest possible answer to #57's "which endpoints are actually free".</b> That
    /// scope item asks for the free classification to be established rather than assumed, and
    /// warns that "these are documentation <c>GET</c>s" is a prior rather than a probe. An endpoint
    /// that never looks at a credential cannot attribute a request to an account, and what cannot
    /// be attributed cannot be billed. That reasoning does not depend on a pricing page, and it
    /// holds for anyone's key rather than only for the account this ran under.
    /// </para>
    /// <para>
    /// <b>And it makes the fixture re-capture instructions in <c>Data/README.md</c> honest.</b>
    /// Re-capturing needs no key, so a contributor can refresh the oracle without an account.
    /// </para>
    /// <para>
    /// <b>What is <em>not</em> claimed here.</b> This says nothing about the endpoints that return
    /// rows. <c>get_range</c> authenticates — measured, <c>401</c>
    /// <c>auth_authentication_failed</c> — and whether this account is entitled to reference
    /// <em>data</em> is a separate question that only <see cref="RealReferenceRequestTests"/> can
    /// ask.
    /// </para>
    /// </remarks>
    [Fact(SkipUnless = nameof(IsConfigured), Skip = ReferenceCredentials.SkipReason)]
    public async Task TheDocumentationEndpoints_AnswerWithoutAValidCredential()
    {
        // Through the client, with a key the API cannot know. The point is that this does not
        // throw: ReferenceClient sends HTTP Basic exactly as the historical client does, and the
        // server declines to check it.
        await using var client = new ReferenceClient { ApiKey = new ApiKey(NotARealKey) };

        var enums = await client.CorporateActions.ListEnumsAsync(Cancel);
        var events = await client.CorporateActions.ListEventsAsync(Cancel);

        Assert.NotEmpty(enums);
        Assert.NotEmpty(events);

        // And with no credential at all, which is the fact the paragraph above rests on and which
        // no ReferenceClient can express — ApiKey is `required`. A bare HttpClient is the only way
        // to ask, and asking is the difference between a probe and an assumption.
        using var bare = new HttpClient { BaseAddress = HistoricalGateway.Bo1.ToUri() };

        foreach (var slug in new[] { "corporate_actions.list_enums", "corporate_actions.list_events" })
        {
            using var response = await bare.GetAsync(
                new Uri($"v{HistoricalClient.ApiVersion}/{slug}", UriKind.Relative), Cancel);

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{slug} answered {(int)response.StatusCode} to an unauthenticated request. It "
                + "answered 200 when this was written, which is what establishes that these two "
                + "endpoints are free rather than merely cheap. If Databento has started "
                + "authenticating them, that argument no longer holds and this class needs its "
                + "free-to-run promise re-established some other way.");
        }
    }

    // ----------------------------------------------------------------------------------------

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>>> LiveEnums()
    {
        await using var client = Client();
        return await ReferenceEntitlement.ExplainingForbidden(
            () => client.CorporateActions.ListEnumsAsync(Cancel));
    }

    private static async Task<IReadOnlyDictionary<string, EventDoc>> LiveEvents()
    {
        await using var client = Client();
        return await ReferenceEntitlement.ExplainingForbidden(
            () => client.CorporateActions.ListEventsAsync(Cancel));
    }

    private static void AssertKnown<T>(
        IReadOnlyDictionary<string, IReadOnlyList<EventEnumVariant>> live, string group)
        where T : struct, IReferenceCode<T>
    {
        Assert.True(live.ContainsKey(group), $"list_enums no longer reports a '{group}' group.");

        var unknown = live[group]
            .Where(v => v.Code is not null && !T.KnownCodes.Contains(v.Code))
            .Select(v => v.Code!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"{typeof(T).Name} does not know {unknown.Count} code(s) the server's '{group}' group "
            + $"reports: {string.Join(", ", unknown)}");
    }

    private static void AssertSameNames(string what, IReadOnlySet<string> live, IReadOnlySet<string> fixture)
    {
        var added = live.Except(fixture, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var removed = fixture.Except(live, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            added.Count == 0 && removed.Count == 0,
            $"The server's {what} no longer match Data/'s vendored copy. "
            + $"Added since capture: {Render(added)}. Gone since capture: {Render(removed)}. "
            + "Re-capture the fixture — see Data/README.md — rather than editing the expectation.");
    }

    private static string? Describe(string group, IReadOnlySet<string> live, IReadOnlySet<string> fixture)
    {
        var added = live.Except(fixture, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var removed = fixture.Except(live, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return added.Count == 0 && removed.Count == 0
            ? null
            : $"  {group}: added {Render(added)}, gone {Render(removed)}";
    }

    private static string Render(List<string> codes) =>
        codes.Count == 0 ? "none" : string.Join(", ", codes);
}
