using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Extensions.Hosting;

/// <summary>
/// Receives the records a live session decodes. Registered with
/// <see cref="DatabentoLiveBuilder.AddRecordHandler{THandler}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two methods, because there is no third option.</b> An <c>async</c> method cannot take a
/// <c>ref struct</c> across an <c>await</c> — CS4007 — so a record can only be handed over
/// synchronously. <see cref="OnRecord"/> is that hand-over and
/// <see cref="OnFlushAsync"/> is where the I/O goes.
/// </para>
/// <para>
/// <b>The alternative costs two allocations per record and was rejected for that.</b>
/// <c>LiveClient.RecordsAsync</c> yields an <c>OwnedRecord</c> and is public; a caller who wants
/// it needs no help from this package. What this package promises is the guarantee
/// <c>LiveAllocationTests</c> asserts, in the one package whose reason to exist is that
/// guarantee.
/// </para>
/// <para>
/// <b>Implementations are singletons.</b> A DI scope per record would allocate and defeat the
/// contract. A handler needing scoped services opens a scope inside <see cref="OnFlushAsync"/>,
/// which is where its I/O belongs anyway.
/// </para>
/// <para>
/// <b>An exception from either method ends the session.</b> Swallowing it would lose market data
/// invisibly, which is the failure class this codebase exists to convert into loud ones. A handler
/// that wants to carry on catches its own.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class TradePrinter : ILiveRecordHandler
/// {
///     private readonly List&lt;string&gt; _batch = [];
///
///     public void OnRecord(scoped RecordRef record)
///     {
///         // Copy out what you need. The RecordRef points into the decoder's buffer and is valid
///         // for this call only — the next fill may shift it.
///         if (record.TryGet(out TradeMsg trade))
///         {
///             _batch.Add($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
///         }
///     }
///
///     public async ValueTask OnFlushAsync(CancellationToken cancellationToken)
///     {
///         if (_batch.Count == 0)
///         {
///             return;   // an already-completed ValueTask allocates nothing
///         }
///
///         await WriteAsync(_batch, cancellationToken);
///         _batch.Clear();
///     }
/// }
/// </code>
/// </example>
public interface ILiveRecordHandler
{
    /// <summary>
    /// Called once per record, inside the drain. <b>The record is valid for this call only.</b>
    /// </summary>
    /// <param name="record">
    /// The record, reinterpreted in place over the decoder's buffer. Copy out what you need; do
    /// not keep the reference.
    /// </param>
    void OnRecord(scoped RecordRef record);

    /// <summary>
    /// Called once per socket fill, after every buffered record has been drained. Where I/O goes.
    /// </summary>
    /// <remarks>
    /// Awaiting an already-completed <see cref="ValueTask"/> allocates nothing, so a handler with
    /// nothing to flush costs nothing.
    /// </remarks>
    /// <param name="cancellationToken">Cancelled when the session is stopping.</param>
    ValueTask OnFlushAsync(CancellationToken cancellationToken);
}
