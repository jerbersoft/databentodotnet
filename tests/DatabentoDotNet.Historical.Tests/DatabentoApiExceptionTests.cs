using System.Net;
using System.Text.Json;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Tests for <see cref="DatabentoApiException"/>.
/// </summary>
public class DatabentoApiExceptionTests
{
    [Fact]
    public void DefaultConstructor_DoesNotThrow()
    {
        var exception = new DatabentoApiException();

        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void MessageConstructor_SetsMessage()
    {
        var exception = new DatabentoApiException("bad request");

        Assert.Equal("bad request", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void MessageAndInnerExceptionConstructor_SetsBoth()
    {
        var inner = new InvalidOperationException("socket closed");

        var exception = new DatabentoApiException("bad request", inner);

        Assert.Equal("bad request", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    // Request id present/absent, crossed with three shapes of the docs-URL/case pair: both
    // present (a structured error with a case), docs only (a structured error without one), and
    // both absent (an unstructured error). Upstream's BusinessErrorDetails
    // (historical/client.rs:47-54) declares `docs: String` non-optional but `case: Option<String>`,
    // so "docs without case" is a real response shape and is tested here; "case without docs" is
    // not tested because no server response produces it, not because ComposeMessage treats the
    // two as joined. Order and every literal fragment ("failed with", " See ",
    // " for documentation.", " (case: ", ")") are upstream's, not ours; the rendering of
    // statusCode itself ("{(int)statusCode} {statusCode}") is this port's own call, pinned as a
    // fully literal string in Message_ForAFullyPopulatedResponse_MatchesTheLiteralExpectedText
    // below rather than only re-derived here.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Message_ComposesFromTheResponseInUpstreamsOrder(bool hasRequestId, bool hasDocsUrl, bool hasErrorCase)
    {
        var requestId = hasRequestId ? "3igAo1qb9YMhu9M" : null;
        var docsUrl = hasDocsUrl ? "https://databento.com/docs/errors/auth_fail" : null;
        var errorCase = hasErrorCase ? "auth_fail" : null;

        var exception = new DatabentoApiException(
            HttpStatusCode.Unauthorized,
            requestId,
            errorCase,
            "invalid API key",
            docsUrl,
            payload: null);

        var status = $"{(int)HttpStatusCode.Unauthorized} {HttpStatusCode.Unauthorized}";
        var docs = hasDocsUrl ? $" See {docsUrl} for documentation." : string.Empty;
        var @case = hasErrorCase ? $" (case: {errorCase})" : string.Empty;
        var expected = hasRequestId
            ? $"{requestId} failed with {status} invalid API key{docs}{@case}"
            : $"{status} invalid API key{docs}{@case}";

        Assert.Equal(expected, exception.Message);
    }

    // Message_ComposesFromTheResponseInUpstreamsOrder re-derives ComposeMessage's own logic to
    // check its shape across every combination; this pins one full response's Message as a fully
    // literal string, so the rendering this port chose for statusCode — the number and the BCL's
    // PascalCase name, not upstream's canonical reason-phrase text (see DatabentoApiException's
    // remarks) — is stated somewhere as text rather than only ever re-derived.
    [Fact]
    public void Message_ForAFullyPopulatedResponse_MatchesTheLiteralExpectedText()
    {
        var exception = new DatabentoApiException(
            HttpStatusCode.Unauthorized,
            "3igAo1qb9YMhu9M",
            "auth_fail",
            "invalid API key",
            "https://databento.com/docs/errors/auth_fail",
            payload: null);

        Assert.Equal(
            "3igAo1qb9YMhu9M failed with 401 Unauthorized invalid API key See "
            + "https://databento.com/docs/errors/auth_fail for documentation. (case: auth_fail)",
            exception.Message);
    }

    [Fact]
    public void ResponseConstructor_SetsStatusCodeRequestIdCaseAndDocsUrl()
    {
        var exception = new DatabentoApiException(
            HttpStatusCode.NotFound,
            "3igAo1qb9YMhu9M",
            "not_found",
            "dataset not found",
            "https://databento.com/docs/errors/not_found",
            payload: null);

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("3igAo1qb9YMhu9M", exception.RequestId);
        Assert.Equal("not_found", exception.Case);
        Assert.Equal("https://databento.com/docs/errors/not_found", exception.DocsUrl);
    }

    [Fact]
    public void Payload_IsNull_WhenTheServerSentNone()
    {
        // Never an empty dictionary standing in for absent — upstream's Option<Box<HashMap<..>>>
        // distinguishes the two and so must this property.
        var exception = new DatabentoApiException(
            HttpStatusCode.BadRequest,
            requestId: null,
            errorCase: null,
            message: "bad request",
            docsUrl: null,
            payload: null);

        Assert.Null(exception.Payload);
    }

    [Fact]
    public void Payload_IsClonedAndSurvivesDisposalOfTheSourceDocument()
    {
        // The exception is built *inside* the using block, while the document backing its
        // payload elements is still alive — JsonElement.Clone() itself requires that; cloning
        // from an already-disposed document throws ObjectDisposedException, which is not the
        // failure this test is after. What the test is after is what happens when the *reader*
        // outlives the document, which is the real shape of this path: the client parses the
        // response body into a JsonDocument, builds the payload from it, constructs this
        // exception — cloning happens here — and only then disposes the document once the
        // response has been fully handled.
        DatabentoApiException exception;
        using (var document = JsonDocument.Parse("""{"dataset":"GLBX.MDP3","start":"2024-01-01"}"""))
        {
            var payload = new Dictionary<string, JsonElement>
            {
                ["dataset"] = document.RootElement.GetProperty("dataset"),
                ["start"] = document.RootElement.GetProperty("start"),
            };

            exception = new DatabentoApiException(
                HttpStatusCode.UnprocessableContent,
                requestId: null,
                errorCase: null,
                message: "the date range is outside availability",
                docsUrl: null,
                payload: payload);
        }

        // The document has now left scope and been disposed. If DatabentoApiException had stored
        // the elements as handed in rather than cloning them during construction, the two reads
        // below would be a use-after-dispose rather than a passing assertion.
        Assert.NotNull(exception.Payload);
        Assert.Equal("GLBX.MDP3", exception.Payload["dataset"].GetString());
        Assert.Equal("2024-01-01", exception.Payload["start"].GetString());
    }
}
