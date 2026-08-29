namespace DatabentoDotNet.Reference;

/// <summary>
/// Renders a list of reference codes as the comma-joined form value the <c>countries</c>,
/// <c>events</c> and <c>security_types</c> filters take.
/// </summary>
/// <remarks>
/// Upstream writes this three times, once per filter, each as an <c>AddToForm</c> impl that pushes
/// nothing when the list is empty (<c>reference.rs:252-297</c>). Here it is one generic method,
/// because the three differ only in which type they hold. <see langword="null"/> is the "push
/// nothing" answer, so a caller omits the parameter rather than sending an empty one — the same
/// distinction <c>ReferenceDateTimeRange</c> draws for an absent <c>end</c>.
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
}
