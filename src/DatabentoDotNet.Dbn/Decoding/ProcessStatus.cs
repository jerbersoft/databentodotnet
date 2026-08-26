namespace DatabentoDotNet.Dbn;

/// <summary>
/// What one step of <see cref="DbnFsm.Process(out int, out RecordRef)"/> produced.
/// </summary>
/// <remarks>
/// <para>
/// The port of upstream's <c>ProcessResult&lt;R&gt;</c> (<c>fsm.rs:78-91</c>), which is a
/// data-carrying Rust enum with four variants: <c>ReadMore(usize)</c>, <c>Metadata(Metadata)</c>,
/// <c>Record(R)</c> and <c>Err(Error)</c>. C# has no such enum and emulating one with a class
/// hierarchy would allocate per record, so the payloads move out to <c>out</c> parameters and the
/// enum stays a plain status.
/// </para>
/// <para>
/// There is no <c>Err</c> member. Malformed data is exceptional and throws a
/// <see cref="DbnDecodeException"/>; a stream simply ending is not, and surfaces as
/// <see cref="NeedMoreData"/> followed by the caller observing end-of-stream.
/// </para>
/// </remarks>
public enum ProcessStatus
{
    /// <summary>
    /// Nothing could be decoded yet. Write more bytes into <see cref="DbnFsm.Space"/>, call
    /// <see cref="DbnFsm.Fill"/>, and step again.
    /// </summary>
    NeedMoreData,

    /// <summary>
    /// The stream's metadata block was decoded; it is available from
    /// <see cref="DbnFsm.Metadata"/>. Happens at most once per stream.
    /// </summary>
    Metadata,

    /// <summary>One record was decoded and handed back through the step's <c>out</c> parameter.</summary>
    Record,
}
