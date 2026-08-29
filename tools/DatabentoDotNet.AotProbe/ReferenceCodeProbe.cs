using DatabentoDotNet.Reference;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// The ten reference code tables, inside the native binary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every check here goes through the generic constraint, not through the concrete type.</b>
/// <c>IReferenceCode&lt;TSelf&gt;</c> declares <c>From</c> and <c>KnownCodes</c> as static abstract
/// members, so <see cref="Probe{T}"/> resolves ten separate instantiations of the same code — the
/// construct ILC must generate ahead of time and cannot recover by reflection if it guessed wrong.
/// Calling <c>Country.From</c> directly would compile to a plain static call and prove nothing
/// about that.
/// </para>
/// <para>
/// <b>No count is asserted, deliberately.</b> <c>ReferenceCodeTableTests</c> checks these tables
/// against the vendored <c>list_enums</c> fixture, which is the server's own dictionary and the
/// only thing entitled to say how many codes there are; a number repeated here would be a second
/// expectation to keep in step with the first, and it would fail for the wrong reason after a
/// re-capture. What this probe claims is narrower and is the claim AOT puts in doubt: the tables
/// are populated at all, and lookups through them return the same answers natively.
/// </para>
/// </remarks>
internal static class ReferenceCodeProbe
{
    public static void Run(ProbeReport report)
    {
        ProbeReport.Section("reference: the ten code tables");

        var codes = 0;
        codes += Probe<Country>(report, "US");
        codes += Probe<Currency>(report, "USD");
        codes += Probe<Event>(report, "AGM");
        codes += Probe<EventCategory>(report, null);
        codes += Probe<EventLevel>(report, null);
        codes += Probe<EventSubType>(report, null);
        codes += Probe<FieldGroup>(report, null);
        codes += Probe<Frequency>(report, null);
        codes += Probe<OutturnStyle>(report, null);
        codes += Probe<SecurityType>(report, null);

        ProbeReport.Note($"{codes} known codes across ten tables.");

        // ReferenceCodeFilter is the other generic seam: it renders a filter value from any
        // IReferenceCode, so it too needs one ILC instantiation per type it is ever called with.
        report.RequireEqual(
            "US,GB",
            ReferenceCodeFilter.Render([Country.From("US"), Country.From("GB")]) ?? "<null>",
            "ReferenceCodeFilter renders a Country list");
        report.RequireEqual(
            "USD",
            ReferenceCodeFilter.Render([Currency.From("USD")]) ?? "<null>",
            "ReferenceCodeFilter renders a Currency list");
        report.Require(ReferenceCodeFilter.Render<Country>(null) is null, "ReferenceCodeFilter renders nothing for no values");
    }

    /// <summary>
    /// Exercises one code table through its static abstract interface members.
    /// </summary>
    /// <typeparam name="T">The code type.</typeparam>
    /// <param name="report">Where the checks land.</param>
    /// <param name="wellKnown">
    /// A code this type is expected to know, or <see langword="null"/> to take the first one the
    /// table itself reports. Naming one where a stable example exists makes the check specific;
    /// taking the table's own first entry everywhere else keeps this from becoming an eleventh copy
    /// of the tables.
    /// </param>
    /// <returns>How many codes the type knows.</returns>
    private static int Probe<T>(ProbeReport report, string? wellKnown)
        where T : struct, IReferenceCode<T>
    {
        var name = typeof(T).Name;
        var known = T.KnownCodes;

        report.Require(known.Count > 0, $"{name}.KnownCodes is populated");
        if (known.Count == 0)
        {
            return 0;
        }

        var sample = wellKnown ?? known.OrderBy(code => code, StringComparer.Ordinal).First();
        report.Require(known.Contains(sample), $"{name}.KnownCodes contains '{sample}'");

        var value = T.From(sample);
        report.Require(value.IsKnown, $"{name}.From('{sample}') is known");
        report.Require(value.HasValue, $"{name}.From('{sample}') has a value");
        report.RequireEqual(sample, value.Code ?? "<null>", $"{name}.From('{sample}').Code round-trips");

        // A blank is a value in this model rather than a malformed one, and an unrecognised code is
        // carried rather than rejected — both are decisions the reference client makes on every
        // response, so both are worth confirming natively.
        var blank = T.From(null);
        report.Require(!blank.HasValue, $"{name}.From(null) carries no value");
        report.Require(!blank.IsKnown, $"{name}.From(null) is not known");

        var unknown = T.From("ZZZZZZ");
        report.Require(unknown.HasValue, $"{name}.From('ZZZZZZ') still carries the code");
        report.Require(!unknown.IsKnown, $"{name}.From('ZZZZZZ') is not known");

        return known.Count;
    }
}
