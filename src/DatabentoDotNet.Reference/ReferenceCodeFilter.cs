namespace DatabentoDotNet.Reference;

/// <summary>
/// Renders a list of reference codes as the comma-joined form value the <c>countries</c>,
/// <c>events</c>, <c>security_types</c> and <c>exchanges</c> filters take.
/// </summary>
/// <remarks>
/// <para>
/// Upstream writes this four times, once per filter, each as an <c>AddToForm</c> impl that pushes
/// nothing when the list is empty (<c>reference.rs:252-297</c>, and <c>corporate.rs:99-107</c> for
/// the fourth). Here it is two methods, because the four differ only in which type they hold —
/// three of them a reference code and the fourth a bare string. <see langword="null"/> is the "push
/// nothing" answer in both, so a caller omits the parameter rather than sending an empty one — the
/// same distinction <see cref="ReferenceDateTimeRange"/> draws for an absent <c>end</c>.
/// </para>
/// <para>
/// <b>The <c>exchanges</c> overload is separate because an exchange code is not an
/// <see cref="IReferenceCode{T}"/>.</b> Upstream has the same problem and solves it the same way:
/// <c>Vec&lt;String&gt;</c> is a foreign type it cannot write an <c>AddToForm</c> impl for, so
/// <c>corporate.rs</c> wraps it in a private <c>Exchanges</c> newtype — the one form renderer that
/// lives in that file rather than in <c>reference.rs</c>. There is no set of exchange codes to
/// close over: <c>list_enums</c> reports no group for them, and <see cref="CorporateAction.Exchange"/>
/// is a bare <see cref="string"/> for that reason.
/// </para>
/// </remarks>
public static class ReferenceCodeFilter
{
    /// <summary>
    /// Joins <paramref name="values"/> with commas, or returns <see langword="null"/> when there is
    /// nothing to filter on and the parameter should be left out entirely.
    /// </summary>
    /// <typeparam name="T">The reference code type.</typeparam>
    /// <param name="values">The codes to filter for, or <see langword="null"/> for no filter.</param>
    /// <returns>The form value, or <see langword="null"/> to omit the parameter.</returns>
    /// <exception cref="ArgumentException">
    /// One of <paramref name="values"/> names no code. A <see langword="default"/> in a filter list
    /// is a caller mistake rather than an empty filter: dropping it would silently widen the query,
    /// and sending it would produce a stray comma.
    /// </exception>
    public static string? Render<T>(IEnumerable<T>? values)
        where T : struct, IReferenceCode<T>
    {
        if (values is null)
        {
            return null;
        }

        var codes = new List<string>();

        foreach (var value in values)
        {
            codes.Add(value.Code ?? throw new ArgumentException(
                $"A {typeof(T).Name} at index {codes.Count} of this filter names no code. "
                + "A filter list holds values to match; leave the list empty or null to match everything.",
                nameof(values)));
        }

        return codes.Count == 0 ? null : string.Join(',', codes);
    }

    /// <summary>
    /// Joins <paramref name="values"/> with commas, or returns <see langword="null"/> when there is
    /// nothing to filter on and the parameter should be left out entirely. The overload the
    /// <c>exchanges</c> filter uses.
    /// </summary>
    /// <remarks>
    /// The refusal below is the same rule the generic overload applies to a <see langword="default"/>
    /// code, restated for the one filter whose values are not codes: a blank entry would widen the
    /// query if dropped and produce a stray comma if sent. Upstream joins without checking
    /// (<c>corporate.rs:101-106</c>), which is the behaviour, not the contract — it has no empty
    /// <c>String</c> to worry about in its own tests.
    /// </remarks>
    /// <param name="values">The exchange codes to filter for, or <see langword="null"/> for no filter.</param>
    /// <returns>The form value, or <see langword="null"/> to omit the parameter.</returns>
    /// <exception cref="ArgumentException">
    /// One of <paramref name="values"/> is <see langword="null"/>, empty, or white space.
    /// </exception>
    public static string? Render(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var codes = new List<string>();

        foreach (var value in values)
        {
            codes.Add(string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(
                    $"The exchange at index {codes.Count} of this filter is blank. "
                    + "A filter list holds values to match; leave the list empty or null to match everything.",
                    nameof(values))
                : value);
        }

        return codes.Count == 0 ? null : string.Join(',', codes);
    }
}
