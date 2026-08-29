using System.Text.Json;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// One entry in an enum group as <c>corporate_actions.list_enums</c> reports it.
/// </summary>
/// <param name="Code">
/// The wire code, or <see langword="null"/> — which is a value rather than a hole. A null code
/// means a blank is legal for the field, which is why the corresponding model fields are nullable.
/// </param>
/// <param name="Description">The server's own description of the code.</param>
public readonly record struct ReferenceEnumVariant(string? Code, string Description);

/// <summary>
/// One event as <c>corporate_actions.list_events</c> documents it, reduced to the parts that are
/// an enum authority.
/// </summary>
/// <param name="Code">The event code — <c>AGM</c> — which is also the key it arrived under.</param>
/// <param name="Category">The <c>EventCategory</c> value, or <see langword="null"/>.</param>
/// <param name="Level">The <c>EventLevel</c> value.</param>
/// <param name="FieldGroups">The distinct <c>FieldGroup</c> values across this event's fields.</param>
/// <param name="SubtypeCodes">
/// The distinct <c>EventSubType</c> codes this event declares. A null code is dropped here: it
/// means "generic event, no subtype provided" rather than naming a subtype.
/// </param>
public sealed record ReferenceEventDoc(
    string Code,
    string? Category,
    string? Level,
    IReadOnlySet<string> FieldGroups,
    IReadOnlySet<string> SubtypeCodes);

/// <summary>
/// The vendored <c>corporate_actions.list_enums</c> and <c>corporate_actions.list_events</c>
/// responses, read as plain dictionaries — the oracle the reference enums are checked against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here parses the fixtures with this library's own reference models or JSON
/// converters, and nothing may.</b> Those are the code these files exist to check, and an oracle
/// read by the code it checks is not an oracle — the same argument
/// <c>tests/DatabentoDotNet.Historical.Tests/BannedSymbols.txt</c> makes for
/// <c>MetadataEncoder</c>, and the same one that keeps <c>MockHistoricalGateway</c> from using the
/// client it tests. <see cref="JsonDocument"/> and <see langword="string"/> are the whole
/// vocabulary.
/// </para>
/// <para>
/// See <c>Data/README.md</c> for where these files came from, what is in them, and how to
/// re-capture them. The short version: they are the live API's own responses, captured by this
/// repository rather than vendored from an upstream repository, because upstream's
/// <c>enums.rs</c> is behind the API on three of the ten enums this library models.
/// </para>
/// <para>
/// Loaded once and cached. The larger file is ~879 KB and parses to 13,123 entries; re-reading it
/// per test class would cost more than the tests themselves.
/// </para>
/// </remarks>
public sealed class ReferenceEnumFixture
{
    /// <summary>The file <see cref="Groups"/> is read from.</summary>
    public const string EnumsFileName = "corporate_actions.list_enums.json";

    /// <summary>The file <see cref="Events"/> is read from.</summary>
    public const string EventsFileName = "corporate_actions.list_events.json";

    private static readonly Lazy<ReferenceEnumFixture> Cached =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private ReferenceEnumFixture(
        IReadOnlyDictionary<string, IReadOnlyList<ReferenceEnumVariant>> groups,
        IReadOnlyDictionary<string, ReferenceEventDoc> events)
    {
        Groups = groups;
        Events = events;
    }

    /// <summary>The fixtures, parsed once for the whole test run.</summary>
    public static ReferenceEnumFixture Instance => Cached.Value;

    /// <summary>
    /// The directory the fixtures are copied to, rooted at the running assembly's own output
    /// folder rather than at the current directory, which the runner chooses.
    /// </summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>
    /// Every enum group the server reports, keyed by group name — <c>CNTRY</c>, <c>SECTYPE</c> —
    /// in the order the response listed them.
    /// </summary>
    /// <remarks>
    /// This is the corporate actions data dictionary, far broader than the ten enums this library
    /// types. That breadth is expected: it is also why <c>CorporateAction</c>'s <c>date_info</c>,
    /// <c>rate_info</c> and <c>event_info</c> stay open maps.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<ReferenceEnumVariant>> Groups { get; }

    /// <summary>Every documented event, keyed by its code.</summary>
    /// <remarks>
    /// The only authority for <c>EventCategory</c>, <c>EventLevel</c> and <c>FieldGroup</c>:
    /// <c>list_enums</c> has no group for any of the three.
    /// </remarks>
    public IReadOnlyDictionary<string, ReferenceEventDoc> Events { get; }

    /// <summary>
    /// The distinct non-null codes in one group, which is what an enum table is compared against.
    /// </summary>
    /// <remarks>
    /// Distinct, because a group may repeat a code: <c>EVENTSUBTYPE</c> has 80 entries and 67
    /// codes, six of them appearing more than once with a description that depends on the parent
    /// event. Non-null, because a blank is a property of the field rather than a member of the
    /// enum — <see cref="HasBlank"/> reports that separately.
    /// </remarks>
    /// <param name="group">The group name.</param>
    /// <returns>The group's distinct codes.</returns>
    /// <exception cref="KeyNotFoundException">No group by that name.</exception>
    public IReadOnlySet<string> CodesIn(string group) =>
        Groups[group].Where(v => v.Code is not null).Select(v => v.Code!).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a group lists a null-code entry, meaning a blank is legal for the field.</summary>
    /// <param name="group">The group name.</param>
    /// <returns><see langword="true"/> when at least one entry has no code.</returns>
    /// <exception cref="KeyNotFoundException">No group by that name.</exception>
    public bool HasBlank(string group) => Groups[group].Any(v => v.Code is null);

    /// <summary>The distinct <c>EventCategory</c> values across every documented event.</summary>
    public IReadOnlySet<string> EventCategories => Distinct(e => e.Category);

    /// <summary>The distinct <c>EventLevel</c> values across every documented event.</summary>
    public IReadOnlySet<string> EventLevels => Distinct(e => e.Level);

    /// <summary>The distinct <c>FieldGroup</c> values across every documented event.</summary>
    public IReadOnlySet<string> FieldGroups =>
        Events.Values.SelectMany(e => e.FieldGroups).ToHashSet(StringComparer.Ordinal);

    private static ReferenceEnumFixture Load()
    {
        using var enums = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, EnumsFileName)));
        using var events = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory, EventsFileName)));

        var groups = new Dictionary<string, IReadOnlyList<ReferenceEnumVariant>>(StringComparer.Ordinal);
        foreach (var group in enums.RootElement.EnumerateObject())
        {
            var variants = new List<ReferenceEnumVariant>();
            foreach (var variant in group.Value.EnumerateArray())
            {
                variants.Add(new ReferenceEnumVariant(
                    Text(variant, "code"),
                    Text(variant, "description") ?? string.Empty));
            }

            groups[group.Name] = variants;
        }

        var docs = new Dictionary<string, ReferenceEventDoc>(StringComparer.Ordinal);
        foreach (var doc in events.RootElement.EnumerateObject())
        {
            docs[doc.Name] = new ReferenceEventDoc(
                doc.Name,
                Text(doc.Value, "category"),
                Text(doc.Value, "level"),
                Values(doc.Value, "fields", "group"),
                Values(doc.Value, "subtypes", "code"));
        }

        return new ReferenceEnumFixture(groups, docs);
    }

    // A property that is absent and one that is present-but-null both mean "not given" here; the
    // response uses the two interchangeably and nothing downstream distinguishes them.
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HashSet<string> Values(JsonElement element, string array, string property)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (element.TryGetProperty(array, out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Text(item, property) is { } value)
                {
                    found.Add(value);
                }
            }
        }

        return found;
    }

    private HashSet<string> Distinct(Func<ReferenceEventDoc, string?> select) =>
        Events.Values.Select(select).Where(v => v is not null).Select(v => v!).ToHashSet(StringComparer.Ordinal);
}
