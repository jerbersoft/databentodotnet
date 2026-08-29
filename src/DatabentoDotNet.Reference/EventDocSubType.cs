namespace DatabentoDotNet.Reference;

/// <summary>One subtype an event can be narrowed to, and what that subtype means.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>EventDocSubType</c> (<c>corporate.rs:463-470</c>).
/// </para>
/// <para>
/// <b><see cref="Code"/> is <see cref="EventSubType"/> and not <c>EventSubType?</c>, and that is
/// this library's spelling of upstream's <c>Option</c> rather than a departure from it.</b> The ten
/// open code carriers already have a value meaning "no value": <see langword="default"/>, whose
/// <see cref="EventSubType.HasValue"/> is <see langword="false"/> and whose
/// <see cref="EventSubType.Code"/> is <see langword="null"/>. A <see cref="Nullable{T}"/> on top
/// would give the same absence two spellings and force every caller to check both. The five
/// carriers on <c>SecurityMaster</c> and <c>AdjustmentFactor</c> make the same call; the one place
/// this library does reach for a <see cref="Nullable{T}"/> is <c>SecurityMaster.Voting</c>, and
/// only because a closed enum's <see langword="default"/> is an <em>undefined</em> byte rather than
/// an absence.
/// </para>
/// <para>
/// The live endpoint returned 77 subtype entries across 60 events, seven of them with a
/// <see langword="null"/> code — so the absent case is real and not a theoretical one.
/// </para>
/// </remarks>
public sealed record EventDocSubType
{
    /// <summary>
    /// The subtype's code, or <see langword="default"/> when the server sent none.
    /// </summary>
    public EventSubType Code { get; init; }

    /// <summary>What the subtype means.</summary>
    public required string Description { get; init; }
}
