using System.Reflection;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Live;

/// <summary>
/// The <c>client=</c> string the live gateway is told on authentication.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>USER_AGENT</c> (<c>lib.rs:288-295</c>), which formats
/// <c>Databento/{crate version} Rust {os}-{arch}</c>. **The leading token is deliberately
/// <c>DatabentoDotNet</c>, not <c>Databento</c>**: this is a third-party client, and a user agent
/// that says otherwise would misattribute this library's traffic — and its bugs — to the vendor's
/// own clients in their logs. Same reason the package is not <c>Databento.*</c>; see CLAUDE.md,
/// "Naming".
/// </para>
/// <para>
/// The platform token is .NET's runtime identifier — <c>osx-arm64</c>, <c>linux-x64</c>,
/// <c>win-x64</c> — which carries the OS and the architecture that upstream reports separately.
/// </para>
/// </remarks>
public static class UserAgent
{
    /// <summary>The user agent for this build, computed once.</summary>
    public static string Value { get; } = Build();

    private static string Build()
    {
        var version = typeof(UserAgent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        // SourceLink appends '+{commit sha}' to the informational version. That is 40 characters
        // of no interest to a gateway, on a line whose length is the client's problem.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];
        }

        return $"DatabentoDotNet/{version} .NET {RuntimeInformation.RuntimeIdentifier}";
    }
}
