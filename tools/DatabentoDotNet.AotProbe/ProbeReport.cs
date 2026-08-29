using System.Globalization;

namespace DatabentoDotNet.AotProbe;

/// <summary>
/// Collects the probe's checks and its failures, and prints them.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an assertion library. A probe that threw on the first failure would report one
/// broken path per run, and the interesting failure mode for a Native AOT binary is a <em>set</em>
/// of paths breaking together — a trimmed-away JSON context takes every endpoint with it. So every
/// check runs, every failure is collected, and the exit code is the verdict.
/// </para>
/// <para>
/// <see cref="Checks"/> is printed with the failures because zero failures out of zero checks is
/// the shape a silently-trimmed probe would report, and it is indistinguishable from success unless
/// the count is on screen.
/// </para>
/// </remarks>
internal sealed class ProbeReport
{
    private readonly List<string> _failures = [];

    /// <summary>How many checks have run.</summary>
    public int Checks { get; private set; }

    /// <summary>What failed, in the order it was checked.</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>Records one check. <paramref name="claim"/> is stated as the thing that should be true.</summary>
    public void Require(bool condition, string claim)
    {
        Checks++;
        if (!condition)
        {
            _failures.Add(claim);
            Console.WriteLine($"    FAIL  {claim}");
        }
    }

    /// <summary>Records one check comparing two values, reporting both when they differ.</summary>
    public void RequireEqual<T>(T expected, T actual, string what)
        where T : IEquatable<T>
        => Require(expected.Equals(actual), $"{what}: expected {Text(expected)}, got {Text(actual)}");

    /// <summary>Prints an indented line of evidence. Not a check — see <see cref="Require"/>.</summary>
    public static void Note(string line) => Console.WriteLine($"    {line}");

    /// <summary>Announces the section that follows.</summary>
    public static void Section(string title) => Console.WriteLine($"\n[{title}]");

    private static string Text<T>(T value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? "<null>";
}
