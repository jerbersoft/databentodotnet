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

    // The four combinations the brief asks for: request id present/absent, crossed with docs and
    // case present/absent as one axis — upstream's Display never has one without the other, since
    // both come from the same structured BusinessErrorDetails. Order and every literal fragment
    // ("failed with", " See ", " for documentation.", " (case: ", ")") are upstream's, not ours.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Message_ComposesFromTheResponseInUpstreamsOrder(bool hasRequestId, bool hasDocsAndCase)
    {
        var requestId = hasRequestId ? "3igAo1qb9YMhu9M" : null;
        var errorCase = hasDocsAndCase ? "auth_fail" : null;
        var docsUrl = hasDocsAndCase ? "https://databento.com/docs/errors/auth_fail" : null;

        var exception = new DatabentoApiException(
            HttpStatusCode.Unauthorized,
            requestId,
            errorCase,
            "invalid API key",
            docsUrl,
            payload: null);

        var docs = hasDocsAndCase ? $" See {docsUrl} for documentation." : string.Empty;
        var @case = hasDocsAndCase ? $" (case: {errorCase})" : string.Empty;
        var expected = hasRequestId
            ? $"{requestId} failed with {HttpStatusCode.Unauthorized} invalid API key{docs}{@case}"
            : $"{HttpStatusCode.Unauthorized} invalid API key{docs}{@case}";

        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void ResponseConstructor_SetsEveryProperty()
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
