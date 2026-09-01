using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting.Tests;

/// <summary>
/// Records what it was handed, in order, so a test can assert both the records and where the
/// flushes fell between them.
/// </summary>
/// <remarks>
/// <b>It copies the sequence number out of the record and keeps nothing else.</b> A
/// <see cref="RecordRef"/> is valid for the duration of the <see cref="OnRecord"/> call and no
/// longer — it points into the decoder's buffer, which the next fill may shift. A handler that
/// stored one would be the exact mistake this contract's <c>scoped</c> keyword exists to make a
/// compile error.
/// </remarks>
internal sealed class RecordingHandler : ILiveRecordHandler
{
    private readonly List<string> _events = [];

    /// <summary>Each record and each flush, interleaved in the order they happened.</summary>
    public IReadOnlyList<string> Events => _events;

    /// <summary>The sequence numbers seen, in order.</summary>
    public List<uint> Sequences { get; } = [];

    /// <summary>How many times <see cref="OnFlushAsync"/> was called.</summary>
    public int Flushes { get; private set; }

    /// <summary>Thrown from the next <see cref="OnRecord"/> when set.</summary>
    public Exception? ThrowOnRecord { get; set; }

    /// <summary>Thrown from the next <see cref="OnFlushAsync"/> when set.</summary>
    public Exception? ThrowOnFlush { get; set; }

    public void OnRecord(scoped RecordRef record)
    {
        if (ThrowOnRecord is { } fault)
        {
            throw fault;
        }

        if (record.TryGet<MboMsg>(out var mbo))
        {
            Sequences.Add(mbo.Sequence);
            _events.Add($"record:{mbo.Sequence}");
        }
        else
        {
            _events.Add("record:other");
        }
    }

    public ValueTask OnFlushAsync(CancellationToken cancellationToken)
    {
        Flushes++;
        _events.Add("flush");

        return ThrowOnFlush is { } fault
            ? ValueTask.FromException(fault)
            : ValueTask.CompletedTask;
    }
}
