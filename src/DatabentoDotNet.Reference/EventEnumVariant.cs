namespace DatabentoDotNet.Reference;

/// <summary>One code in one of the enum groups <c>corporate_actions.list_enums</c> reports.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>EventEnumVariant</c> (<c>corporate.rs:507-514</c>).
/// </para>
/// <para>
/// <b><see cref="Code"/> is a <see cref="string"/> and deliberately not one of the ten code
/// carriers.</b> Which type a variant belongs to is decided by the group name it was filed under —
/// the dictionary key on <see cref="CorporateActionsClient.ListEnumsAsync"/>, not by anything on
/// the variant — so there is no type to parse it into without first knowing that key. Upstream
/// makes the same call for the same reason, and it is what lets a group this library has never
/// heard of round-trip intact.
/// </para>
/// <para>
/// <b><see cref="Code"/> is <see langword="null"/> for the blank entry a group may carry.</b> 148
/// of the 235 groups the live endpoint returned list one — 154 entries in all — which is the
/// evidence behind the ten carriers reading a blank as "no value" rather than as a malformed code.
/// <see cref="Description"/> is never <see langword="null"/> in that response, and upstream types
/// it non-optional; both are kept.
/// </para>
/// </remarks>
public sealed record EventEnumVariant
{
    /// <summary>
    /// The variant's code, or <see langword="null"/> for a group's blank entry.
    /// </summary>
    public string? Code { get; init; }

    /// <summary>What the code means.</summary>
    public required string Description { get; init; }
}
