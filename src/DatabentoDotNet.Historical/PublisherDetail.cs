namespace DatabentoDotNet.Historical;

/// <summary>The details about a publisher.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>PublisherDetail</c> (<c>databento-rs/src/historical/metadata.rs:231-241</c>).
/// Returned by <c>metadata.list_publishers</c>, which takes no parameters and returns every
/// publisher Databento currently defines.
/// </para>
/// <para>
/// <b>Deliberately not mapped onto <see cref="DatabentoDotNet.Dbn.Publishers.Publisher"/> at the
/// DTO boundary.</b> That table — and the matching <see cref="DatabentoDotNet.Dbn.Publishers.Dataset"/>
/// and <see cref="DatabentoDotNet.Dbn.Publishers.Venue"/> tables — is generated from the
/// <c>dbn</c> crate's <c>publishers.rs</c> at a pinned version, not from this endpoint, so it is
/// pinned to a release rather than tracking the live API. A publisher this response names that the
/// table cannot is Databento having shipped something newer than that pin, which is their news
/// rather than a bug here. So this type keeps the raw wire values, and a caller who wants the enum
/// asks for it explicitly — through
/// <see cref="DatabentoDotNet.Dbn.Publishers.PublisherValues.TryFromPublisher"/> and
/// <see cref="DatabentoDotNet.Dbn.Publishers.PublisherWireStrings"/> — and decides what to do when
/// the lookup fails.
/// </para>
/// </remarks>
public sealed record PublisherDetail
{
    /// <summary>
    /// The publisher ID assigned by Databento, which denotes the dataset and venue.
    /// </summary>
    public required ushort PublisherId { get; init; }

    /// <summary>The dataset code for the publisher.</summary>
    public required string Dataset { get; init; }

    /// <summary>The venue for the publisher.</summary>
    public required string Venue { get; init; }

    /// <summary>The publisher description.</summary>
    public required string Description { get; init; }
}
