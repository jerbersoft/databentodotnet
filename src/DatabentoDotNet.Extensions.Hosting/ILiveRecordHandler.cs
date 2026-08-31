using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting;

// Placeholder: this task fixes the signature so DatabentoLiveBuilder and the registration surface
// compile against it. Task 7 documents it and drives it from the live session runner; it does not
// change this signature.

/// <summary>Receives the records a live session decodes.</summary>
public interface ILiveRecordHandler
{
    /// <summary>Called once per record, inside the drain.</summary>
    void OnRecord(scoped RecordRef record);

    /// <summary>Called once per socket fill, after the drain.</summary>
    ValueTask OnFlushAsync(CancellationToken cancellationToken);
}
