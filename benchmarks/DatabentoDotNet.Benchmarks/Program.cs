using BenchmarkDotNet.Running;

namespace DatabentoDotNet.Benchmarks;

/// <summary>
/// Entry point for the benchmark suite.
/// </summary>
/// <remarks>
/// <para>
/// Run it from the repository root:
/// </para>
/// <code>
/// dotnet run -c Release --project benchmarks/DatabentoDotNet.Benchmarks -- --filter '*'
/// </code>
/// <para>
/// Release only — BenchmarkDotNet refuses a Debug build, and rightly: the numbers would be
/// meaningless. It is not part of <c>dotnet test</c> and ships nothing; see the project file.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Runs whichever benchmarks the command line selects.</summary>
    /// <param name="args">BenchmarkDotNet's own arguments — <c>--filter</c>, <c>--list</c>, …</param>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
