using System.Net;
using System.Net.Http.Headers;
using SignRelay.Contracts;

namespace SignRelay.Tests;

public sealed class HttpTransferTests
{
    [Fact]
    public async Task Retries_503_then_succeeds()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://relay.test/") };
        using var resp = await HttpTransfer.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "/file"),
            "download",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Does_not_retry_400()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("") });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://relay.test/") };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => HttpTransfer.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "/file"),
            "unsigned download [0] bin/x64/dshidmini/dshidmini.dll",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero));

        Assert.Equal(1, handler.Calls);
        Assert.Contains("400", ex.Message);
        Assert.Contains("Body: (empty)", ex.Message);
    }

    [Fact]
    public async Task Exhausts_retries_on_persistent_500()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://relay.test/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpTransfer.SendWithRetryAsync(
            http,
            () => new HttpRequestMessage(HttpMethod.Get, "/file"),
            "download",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero));

        Assert.Equal(HttpTransfer.MaxAttempts, handler.Calls);
    }

    [Fact]
    public void Honors_retry_after_when_computing_delay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), HttpTransfer.ParseRetryAfter(response.Headers));
        Assert.Equal(TimeSpan.FromSeconds(3), HttpTransfer.DelayForAttempt(0, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Download_cleans_temp_file_after_failure()
    {
        var dest = Path.Combine(Path.GetTempPath(), "signrelay-xfer-" + Guid.NewGuid().ToString("N"), "out.bin");
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("no") });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://relay.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpTransfer.DownloadToFileAsync(
            http,
            "file",
            dest,
            "download",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero));

        var dir = Path.GetDirectoryName(dest)!;
        if (Directory.Exists(dir))
        {
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.False(File.Exists(dest));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Reconstructs_content_on_each_upload_attempt()
    {
        var builds = 0;
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("wait") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://relay.test/") };
        using var resp = await HttpTransfer.SendWithRetryAsync(
            http,
            () =>
            {
                builds++;
                return new HttpRequestMessage(HttpMethod.Post, "/signed")
                {
                    Content = new StringContent($"body-{builds}")
                };
            },
            "signed upload",
            CancellationToken.None,
            delayForAttempt: (_, _) => TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, builds);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(params HttpResponseMessage[] responses) =>
            _responses = new Queue<HttpResponseMessage>(responses);

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No scripted response left.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
