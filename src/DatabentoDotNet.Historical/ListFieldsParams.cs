using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The parameters for <c>metadata.list_fields</c>, which lists the record fields for a schema
/// and encoding.
/// </summary>
/// <remarks>
/// Port of upstream's <c>ListFieldsParams</c> (<c>metadata.rs:245-256</c>).
/// </remarks>
public sealed record ListFieldsParams
{
    /// <summary>The encoding to request fields for.</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>The data record schema to request fields for.</summary>
    public required Schema Schema { get; init; }

    /// <summary>
    /// The dataset used to determine which fields are relevant, or <see langword="null"/> to omit
    /// it from the request.
    /// </summary>
    /// <remarks>
    /// <b>Leaving this unset changes what the API answers, not just what it accepts.</b> Upstream
    /// documents this as a warning on the endpoint itself, not merely on the parameter
    /// (<c>metadata.rs:70-73</c>): when <see cref="Dataset"/> is absent, the API returns the
    /// fields for the <em>latest</em> DBN encoding version, which may differ from a specific
    /// dataset's schema. That is server behavior a caller only otherwise discovers from a wrong
    /// answer.
    /// </remarks>
    public string? Dataset { get; init; }

    /// <summary>
    /// Renders this parameter set as the query string the <c>list_fields</c> endpoint's GET
    /// request carries.
    /// </summary>
    /// <remarks>
    /// The order matches upstream: <c>encoding</c> and <c>schema</c> are always sent, and
    /// <c>dataset</c> follows only when <see cref="Dataset"/> is set (<c>metadata.rs:83-90</c>).
    /// </remarks>
    /// <returns>The query parameters, in upstream's push order.</returns>
    public IReadOnlyList<KeyValuePair<string, string>> ToQueryParameters()
    {
        var parameters = new List<KeyValuePair<string, string>>(3)
        {
            new("encoding", Encoding.ToWireString()),
            new("schema", Schema.ToWireString()),
        };

        if (Dataset is { } dataset)
        {
            parameters.Add(new("dataset", dataset));
        }

        return parameters;
    }
}
