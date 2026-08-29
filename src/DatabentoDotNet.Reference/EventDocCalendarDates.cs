namespace DatabentoDotNet.Reference;

/// <summary>
/// One calendar date an event carries, and the name it goes by on that event.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>EventDocCalendarDates</c> (<c>corporate.rs:454-461</c>). <b>The plural
/// name is upstream's and describes one entry, not a collection</b> — a
/// <see cref="EventDoc.CalendarDates"/> holds a list of these. Kept rather than corrected to
/// <c>EventDocCalendarDate</c> because #56 names the type in its scope and the two spellings would
/// otherwise disagree across the issue, the porting guide and the code.
/// </para>
/// <para>
/// <b><see cref="Alias"/> is the interesting half.</b> A corporate action's dates all arrive in
/// <c>CorporateAction</c>'s <c>date_info</c> map under generic names — <c>event_date</c>,
/// <c>ex_date</c> — and this is where the event says what its own <c>event_date</c> means. An
/// <c>AGM</c>'s is a <c>meeting_date</c>; a <c>BB</c>'s is not. Absent for 283 of the 352 entries
/// the live endpoint returned, so it is a genuine <see langword="null"/> rather than a field the
/// server always fills.
/// </para>
/// </remarks>
public sealed record EventDocCalendarDates
{
    /// <summary>
    /// What this date is called on this event, or <see langword="null"/> when it has no name
    /// beyond <see cref="Name"/>.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>The generic name of the date, as it appears in <c>date_info</c>.</summary>
    public required string Name { get; init; }
}
