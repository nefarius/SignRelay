using System.Net;
using System.Net.Http.Headers;
using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class HttpFailureDetailsTests
{
    [Fact]
    public void Format_includes_empty_body_and_status()
    {
        var text = HttpFailureDetails.Format(
            "unsigned download [0] bin/x64/dshidmini/dshidmini.dll",
            1,
            3,
            "GET",
            "/api/v1/worker/jobs/9694ce9d0d174c1db0a5ccde585784b1/unsigned/bin%2Fx64%2Fdshidmini%2Fdshidmini.dll",
            400,
            "Bad Request",
            ["Content-Length: 0"],
            body: "",
            bodyReadError: null);

        Assert.Contains("HTTP failure: unsigned download", text);
        Assert.Contains("attempt 1/3", text);
        Assert.Contains("GET ", text);
        Assert.Contains("400 Bad Request", text);
        Assert.Contains("Body: (empty)", text);
        Assert.Contains("bin%2Fx64", text);
    }

    [Fact]
    public void Format_includes_body_and_body_read_error()
    {
        var text = HttpFailureDetails.Format(
            "signed download",
            2,
            3,
            "GET",
            "/x",
            500,
            "Internal Server Error",
            null,
            body: "partial",
            bodyReadError: "stream closed");

        Assert.Contains("Body read failed: stream closed", text);
        Assert.Contains("partial", text);
    }

    [Fact]
    public void Persist_marks_truncation_at_16kb()
    {
        var huge = new string('x', HttpFailureDetails.PersistMaxChars + 50);
        var persisted = HttpFailureDetails.Persist(huge);
        Assert.Equal(HttpFailureDetails.PersistMaxChars, persisted.Length);
        Assert.EndsWith(HttpFailureDetails.TruncationMarker, persisted);
        Assert.Equal(huge[..100], HttpFailureDetails.Persist(huge[..100]));
    }

    [Fact]
    public void Sensitive_headers_are_redacted()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc");
        response.Headers.TryAddWithoutValidation("X-Request-Id", "req-1");
        response.Content = new StringContent("nope");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var headers = HttpFailureDetails.SafeResponseHeaders(response.Headers, response.Content.Headers);
        Assert.DoesNotContain(headers, h => h.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.Contains("session=abc", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.StartsWith("X-Request-Id:", StringComparison.OrdinalIgnoreCase));
        Assert.True(HttpFailureDetails.IsSensitiveHeader("Authorization"));
        Assert.True(HttpFailureDetails.IsSensitiveHeader("Cookie"));
    }

    [Fact]
    public async Task FromResponseAsync_reads_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("{\"errors\":[\"Job id is invalid.\"]}"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "/api/v1/jobs/nope/files/0/signed")
        };

        var text = await HttpFailureDetails.FromResponseAsync("signed download", 1, 3, response, CancellationToken.None);
        Assert.Contains("400", text);
        Assert.Contains("Job id is invalid.", text);
        Assert.Contains("signed download", text);
    }
}
