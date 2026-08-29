using System.Net;
using System.Text.Json;
using DatabentoDotNet.Historical;

namespace DatabentoDotNet.Reference.Tests;

/// <summary>
/// Turns a <c>403 Forbidden</c> from the reference API into a failure that says what it means, and
/// which of the three reference datasets it means it about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference data is a separate Databento product, and "the key works for historical" does not
/// imply it works here.</b> #58 established that this account can reach reference
/// <em>documentation</em> — both <c>list_*</c> endpoints answered <c>200</c> — and established
/// nothing at all about the endpoints that return rows, because it did not call them. A developer
/// who runs <see cref="RealReferenceRequestTests"/> on an account without the entitlement gets a
/// bare <c>403</c>, and a bare <c>403</c> from a suite whose other tests pass reads as a bug in the
/// suite rather than as an answer about the account.
/// </para>
/// <para>
/// <b>Measured on 2026-08-29, and this is not hypothetical: the account this was written on is
/// exactly that account.</b> All four billed endpoints answered
/// <c>403 license_reference_dataset_no_subscription</c>. The refusal is free — no rows are returned
/// and nothing is billed — which is why it could be established at all.
/// </para>
/// <para>
/// <b>And it revealed a structure nothing in this repository had modelled: reference data is three
/// subscriptions, not one.</b> The response carries
/// <c>payload.reference_dataset</c> naming which — <c>"corporate actions"</c>,
/// <c>"security master"</c>, <c>"adjustment factors"</c> — and the three messages differ
/// accordingly. So an account can hold one and not the others, and "does this key have reference
/// data" is not a question with a single answer. That name is lifted into the message below,
/// because a developer holding two of the three needs to know which one refused.
/// </para>
/// <para>
/// <b>The 403 is caught and re-reported, not swallowed.</b> The test still fails — an unentitled
/// account cannot answer the questions #57 asks, and a green run would be a lie — but it fails
/// saying which of the two things went wrong. Every other status is left exactly as the client
/// threw it: this class knows one thing, and widening it to "explain HTTP" would make it a second
/// error-message implementation next to <see cref="DatabentoApiException"/>'s.
/// </para>
/// <para>
/// <b>Nothing in here touches the key.</b> The message names the product and the environment
/// variable; the exception it wraps renders only the request id, the server's own case and the
/// payload — <see cref="DatabentoApiException"/> is where that rule is implemented, and this class
/// adds no second path to it.
/// </para>
/// </remarks>
public static class ReferenceEntitlement
{
    /// <summary>
    /// The server's own case string for an unsubscribed reference dataset, as measured.
    /// </summary>
    public const string NoSubscriptionCase = "license_reference_dataset_no_subscription";

    /// <summary>
    /// What a <c>403</c> from a reference endpoint means, in the words a developer reading a red
    /// test needs.
    /// </summary>
    /// <param name="exception">The refusal, whose payload names the dataset when the server sent one.</param>
    /// <returns>The message.</returns>
    public static string Explain(DatabentoApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var dataset =
            exception.Payload?.TryGetValue("reference_dataset", out var named) == true
            && named.ValueKind == JsonValueKind.String
                ? named.GetString()
                : null;

        return $"The reference API answered 403 Forbidden{(dataset is null ? string.Empty : $" for the '{dataset}' dataset")}. "
            + "Reference data is a separate Databento product from historical and live market data, "
            + "and it is three separate subscriptions rather than one — corporate actions, security "
            + "master and adjustment factors are entitled independently, which is why the response "
            + "names the one it refused. An account with full historical access is still refused "
            + "here until the relevant dataset is added to it. This is an answer about the account, "
            + "not a defect in the client: the request was well-formed enough to be authenticated "
            + "and then refused, and it cost nothing because no rows were returned. Check the "
            + "account's reference subscriptions before looking anywhere else, and unset "
            + ReferenceCredentials.RequestVariable + " to stop running these.";
    }

    /// <summary>
    /// Runs a reference call, re-reporting a <c>403</c> with <see cref="Explain"/>.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="call">The call.</param>
    /// <returns>Its result.</returns>
    public static async Task<T> ExplainingForbidden<T>(Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (DatabentoApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(Explain(exception), exception);
        }
    }

    /// <summary>
    /// Drains a streamed reference response into a list, re-reporting a <c>403</c> with
    /// <see cref="Explain"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole response, with no cap.</b> A cap would not reduce the bill — the server prices
    /// the query, not the bytes a client chooses to read — so it would buy nothing except a silent
    /// truncation the assertions downstream could not distinguish from a short answer. The window
    /// these tests query is a month of one symbol, deliberately, and that is where the bound
    /// belongs: on what is asked for, not on what is read back.
    /// </para>
    /// <para>
    /// <b>The <c>403</c> can arrive here rather than at the call.</b> The three <c>get_range</c>
    /// methods return an <see cref="IAsyncEnumerable{T}"/>, so nothing is sent until the first
    /// <c>MoveNextAsync</c> — which means the status arrives during enumeration and a
    /// <see langword="try"/> around the call alone would never see it.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="rows">The stream.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every row, in the order the server sent them.</returns>
    public static async Task<List<T>> CollectAsync<T>(
        IAsyncEnumerable<T> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var collected = new List<T>();

        try
        {
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                collected.Add(row);
            }
        }
        catch (DatabentoApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(Explain(exception), exception);
        }

        return collected;
    }
}
