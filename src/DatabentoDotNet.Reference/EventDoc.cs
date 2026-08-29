namespace DatabentoDotNet.Reference;

/// <summary>
/// The server's own documentation for one corporate action event: what it is, what it populates,
/// and what dates it carries.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>EventDoc</c> (<c>corporate.rs:483-505</c>), returned by
/// <see cref="CorporateActionsClient.ListEventsAsync"/> keyed by event code.
/// </para>
/// <para>
/// <b>Seven of these nine properties are optional upstream, and the live endpoint filled all nine
/// on every one of its 60 events but one.</b> Only <see cref="Subtypes"/> was ever
/// <see langword="null"/> — 41 times. The optionality is kept as upstream declares it rather than
/// tightened to what one capture happened to contain: a server that starts omitting
/// <see cref="Description"/> should hand a caller a <see langword="null"/>, not fail the whole
/// document. The three <c>SecurityMaster</c> optionality disagreements #54 reproduced rather than
/// reconciled are the same rule applied the same way.
/// </para>
/// <para>
/// <b><see cref="Participation"/> stays a <see cref="string"/>, and that was asked rather than
/// assumed.</b> <see cref="MandVolu"/> is a closed enum for what looks like the same concept, and
/// folding one into the other is the shape of the mistake #45 was: two vocabularies that agree in
/// meaning and disagree on the wire. They disagree here. <c>list_enums</c>' <c>MANDVOLU</c> group
/// reports the single characters <c>M</c>, <c>V</c> and <c>W</c>; this field reports
/// <c>mandatory</c>, <c>voluntary</c> and <c>mandatory_voluntary</c>. Not one code is shared, so a
/// <see cref="MandVolu"/> here would reject every value the endpoint actually sends.
/// </para>
/// <para>
/// <b><see cref="Code"/> repeats the dictionary key, and nothing here relies on that.</b> It held
/// for all 60 events in the captured response, which is worth a test and is not worth a contract:
/// <see cref="CorporateActionsClient.ListEventsAsync"/> keys by the string the server filed the
/// document under, so an event whose <see cref="Code"/> is absent or disagrees still arrives under
/// its own key.
/// </para>
/// </remarks>
public sealed record EventDoc
{
    /// <summary>
    /// The dates this event carries, and what each is called on it, or <see langword="null"/> when
    /// the server sent none.
    /// </summary>
    public IReadOnlyList<EventDocCalendarDates>? CalendarDates { get; init; }

    /// <summary>
    /// The event's category — <c>distribution</c>, <c>proposals</c> and six others — or
    /// <see langword="default"/> when the server sent none.
    /// </summary>
    public EventCategory Category { get; init; }

    /// <summary>
    /// The event's own code, or <see langword="default"/> when the server sent none. Normally the
    /// key this document arrived under.
    /// </summary>
    public Event Code { get; init; }

    /// <summary>What the event is, in prose.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The fields this event populates and which of <c>CorporateAction</c>'s three open maps each
    /// lands in, or <see langword="null"/> when the server sent none.
    /// </summary>
    public IReadOnlyList<EventDocField>? Fields { get; init; }

    /// <summary>
    /// What the event applies to — <c>country</c>, <c>issuer</c>, <c>listing</c> or
    /// <c>security</c>.
    /// </summary>
    /// <remarks>
    /// The one code carrier here that upstream types without an <c>Option</c>
    /// (<c>corporate.rs:498</c>), so it is <see langword="required"/>: a document with no
    /// <c>level</c> fails to read rather than arriving with an absent one, which is what upstream's
    /// serde does with the same declaration.
    /// </remarks>
    public required EventLevel Level { get; init; }

    /// <summary>The event's human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the event is mandatory, voluntary, or a mix — <c>mandatory</c>,
    /// <c>voluntary</c> or <c>mandatory_voluntary</c>.
    /// </summary>
    /// <remarks>
    /// A <see cref="string"/> rather than a <see cref="MandVolu"/>, because the two vocabularies
    /// share no code. See this type's remarks.
    /// </remarks>
    public string? Participation { get; init; }

    /// <summary>
    /// The subtypes this event can be narrowed to, or <see langword="null"/> when it has none —
    /// which was the case for 41 of the 60 events the live endpoint returned.
    /// </summary>
    public IReadOnlyList<EventDocSubType>? Subtypes { get; init; }
}
