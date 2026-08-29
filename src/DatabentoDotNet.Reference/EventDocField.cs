namespace DatabentoDotNet.Reference;

/// <summary>
/// One field an event populates, and which of <c>CorporateAction</c>'s three open maps it lands in.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>EventDocField</c> (<c>corporate.rs:472-481</c>). <b>This is the type that
/// makes <c>corporate_actions.list_events</c> worth shipping ahead of the endpoint it documents.</b>
/// <c>CorporateAction</c> carries <c>event_info</c>, <c>date_info</c> and <c>rate_info</c> as open
/// maps whose keys vary by event (#55), and <see cref="Group"/> paired with <see cref="Name"/> is
/// the server's own statement of which key may appear in which map.
/// </para>
/// <para>
/// <b>The three groups are exactly the three maps, checked rather than assumed.</b> Across the 349
/// field entries the live endpoint returned, <see cref="Group"/> took three distinct values —
/// <c>date_info</c>, <c>event_info</c> and <c>rate_info</c> — and <see cref="FieldGroup"/> models
/// those three and no others. A fourth would mean <c>CorporateAction</c> is missing a column, which
/// is why the test asserting it belongs here rather than waiting for #55.
/// </para>
/// </remarks>
public sealed record EventDocField
{
    /// <summary>What the field means.</summary>
    public required string Description { get; init; }

    /// <summary>Which of <c>CorporateAction</c>'s three open maps the field arrives in.</summary>
    public required FieldGroup Group { get; init; }

    /// <summary>The field's key within that map.</summary>
    public required string Name { get; init; }
}
