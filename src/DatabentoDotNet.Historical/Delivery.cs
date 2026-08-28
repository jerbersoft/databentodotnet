namespace DatabentoDotNet.Historical;

/// <summary>How a batch job's files are delivered once it has been processed.</summary>
/// <remarks>
/// Port of upstream's <c>Delivery</c> (<c>batch.rs:414-418</c>). One member, and upstream says why:
/// download is the only mechanism the API supports at present. It is an enum rather than an implied
/// constant because the API takes it as a form field and returns it on every job, so a second
/// mechanism would be a new member here rather than a new shape everywhere.
/// </remarks>
public enum Delivery
{
    /// <summary>The files are downloaded from Databento, which is what <c>BatchClient</c> does.</summary>
    Download,
}
