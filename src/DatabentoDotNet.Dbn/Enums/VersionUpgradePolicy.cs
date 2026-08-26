namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// How to handle decoding DBN data from other versions.
/// </summary>
/// <remarks>
/// Control-flow-only: unlike every other enum in this namespace, this type never appears on the
/// wire, so it has no <see cref="WireStrings"/> entry. Upstream's discriminants start at 1, so
/// there is no numeric zero value to make <see cref="UpgradeToV3"/> (upstream's default) line up
/// with C#'s implicit <c>default(VersionUpgradePolicy)</c> without renumbering away from
/// upstream's exact values — which this port will not do. Callers that need "the default" must
/// reference <see cref="UpgradeToV3"/> explicitly rather than relying on
/// <c>default(VersionUpgradePolicy)</c>, which is the unnamed zero value <c>(VersionUpgradePolicy)0</c>.
/// </remarks>
public enum VersionUpgradePolicy : byte
{
    /// <summary>
    /// Decode data from all supported versions (up to and including
    /// <see cref="DbnConstants.Version"/>) as-is.
    /// </summary>
    AsIs = 1,

    /// <summary>
    /// Decode and convert data from DBN versions prior to version 2 to that version. Decoding
    /// data from newer versions fails.
    /// </summary>
    UpgradeToV2 = 2,

    /// <summary>
    /// Decode and convert data from DBN versions prior to version 3 to that version. Decoding
    /// data from newer versions (when they're introduced) fails.
    /// </summary>
    UpgradeToV3 = 3,
}
