using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DatabentoDotNet.AotProbe;
using NodaTime;

// ---------------------------------------------------------------------------------------------
// DatabentoDotNet.AotProbe — the Native AOT end-to-end check (#64).
//
// The three analyzer properties in Directory.Build.targets have been on since M0 and have shaped
// real decisions: the JSON contexts are source-generated because the reflection overloads fail the
// build, and ZstdSharp.Port was chosen for being pure managed. All of that is compile-time
// analysis. It does not prove ILC accepts the assemblies, that the trimmer keeps what the code
// actually reaches, or that any of it runs.
//
// So this program is published with PublishAot and *executed*, and it reports the same record
// counts DatabentoDotNet.Dbn.Tests already asserts for the same fixtures. A publish that succeeds
// and a binary that runs are two different claims; the milestone needs both.
//
// It is offline and needs no credentials: the corpus is vendored, the HTTP endpoints are answered
// by a loopback socket, and the live session runs against the mock gateway.
// ---------------------------------------------------------------------------------------------

var report = new ProbeReport();
var stopwatch = Stopwatch.StartNew();
// A whole-run ceiling, in milliseconds rather than a TimeSpan: that type is banned repo-wide and
// CancellationTokenSource.CancelAfter(int) takes the same value without it. Duration is what the
// rest of the repository would use, and there is nothing here for it to carry.
using var cancellation = new CancellationTokenSource();
cancellation.CancelAfter((int)Duration.FromMinutes(5).TotalMilliseconds);
var cancellationToken = cancellation.Token;

Console.WriteLine("DatabentoDotNet.AotProbe");
Console.WriteLine($"  runtime        {Environment.Version} on {RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"  process        {Environment.ProcessPath}");
Console.WriteLine($"  dynamic code   {(RuntimeFeature.IsDynamicCodeSupported ? "supported" : "unsupported")}");

// Those three lines are evidence, not a verdict, and the distinction was learned the hard way.
// RuntimeFeature.IsDynamicCodeSupported looked like the obvious in-process way to refuse to claim
// AOT from a JIT run — and it is useless for that here, because PublishAot writes
// "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported": false into
// runtimeconfig.json for the ordinary `dotnet build` output too. `dotnet run` on this project
// therefore reports "unsupported" while running under the JIT.
//
// So nothing in this process can honestly assert it was compiled by ILC. tools/aot-probe.sh does
// it from outside instead: it publishes with PublishAot, checks that what came out is a native
// executable rather than a managed assembly, and then runs it. That is a stronger check than any
// flag, and it is the one the workflow runs.

DbnProbe.Run(report);
ReferenceCodeProbe.Run(report);
await HistoricalFileProbe.RunAsync(report, cancellationToken).ConfigureAwait(false);
await JsonContextProbe.RunAsync(report, cancellationToken).ConfigureAwait(false);
await LiveSessionProbe.RunAsync(report, cancellationToken).ConfigureAwait(false);

stopwatch.Stop();

Console.WriteLine();
if (report.Failures.Count == 0)
{
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"PASS — {report.Checks} checks in {stopwatch.ElapsedMilliseconds} ms."));
    return 0;
}

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"FAIL — {report.Failures.Count} of {report.Checks} checks failed:"));

foreach (var failure in report.Failures)
{
    Console.WriteLine($"  - {failure}");
}

return 1;
