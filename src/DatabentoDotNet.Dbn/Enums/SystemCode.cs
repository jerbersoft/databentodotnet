namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// A <c>SystemMsg</c> code indicating the type of message from the live subscription gateway.
/// </summary>
/// <remarks>
/// Discriminants are non-contiguous: 0 through 4 are sequential, then <see cref="Unset"/> jumps
/// to 255. Upstream marks this type <c>#[non_exhaustive]</c>; Databento may add variants in a
/// future release without that being a breaking change. See <see cref="WireStrings"/> for
/// string conversions; wire strings are the mechanical <c>snake_case</c> of the variant name,
/// with no aliases.
/// </remarks>
public enum SystemCode : byte
{
    /// <summary>A message sent in the absence of other records to indicate the connection remains open.</summary>
    Heartbeat = 0,

    /// <summary>An acknowledgement of a subscription request.</summary>
    SubscriptionAck = 1,

    /// <summary>The gateway has detected this session is falling behind real-time.</summary>
    SlowReaderWarning = 2,

    /// <summary>Indicates a replay subscription has caught up with real-time data.</summary>
    ReplayCompleted = 3,

    /// <summary>Signals that all records for interval-based schemas have been published for the given timestamp.</summary>
    EndOfInterval = 4,

    /// <summary>
    /// No system code was specified, or this record was upgraded from a version 1 struct where
    /// the code field didn't exist.
    /// </summary>
    Unset = 255,
}
