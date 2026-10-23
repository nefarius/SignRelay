using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Options;
using SignRelay.Server.Services;

namespace SignRelay.Tests;

internal sealed class RecordingNotificationQueue : IJobNotificationQueue
{
    public List<JobNotification> Items { get; } = [];
    public void Enqueue(JobNotification notification) => Items.Add(notification);
}

public sealed class JobNotificationTests
{
    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpStatusCode> respond) : HttpMessageHandler
    {
        public List<(Uri Url, string Body, HttpRequestMessage Req)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!, body, request));
            return new HttpResponseMessage(respond(request, body));
        }
    }

    private static JobNotificationDispatcher Make(NotificationOptions n, RecordingHandler h) =>
        new(new JobNotificationQueue(), new FakeFactory(h),
            Options.Create(new SignRelayOptions { Notifications = n }),
            NullLogger<JobNotificationDispatcher>.Instance)
        { RetryDelay = TimeSpan.Zero };

    private static JobNotification Fail() =>
        new("abc", JobStatus.Failed, DateTimeOffset.UtcNow, "bad thing");

    [Fact]
    public async Task Disabled_SendsNothing()
    {
        var h = new RecordingHandler((_, _) => HttpStatusCode.OK);
        await Make(new NotificationOptions(), h).DispatchAsync(Fail(), default);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Discord_And_Gotify_Payloads()
    {
        var h = new RecordingHandler((_, _) => HttpStatusCode.OK);
        var opt = new NotificationOptions
        {
            DiscordWebhookUrl = "https://discord.test/api/webhooks/1/secret",
            GotifyBaseUrl = "https://gotify.test/",
            GotifyToken = "tok123"
        };
        await Make(opt, h).DispatchAsync(Fail(), default);

        Assert.Equal(2, h.Calls.Count);
        var discord = h.Calls.Single(c => c.Url.Host == "discord.test");
        Assert.Contains("Signing failed", discord.Body);
        Assert.Contains("bad thing", discord.Body);

        var gotify = h.Calls.Single(c => c.Url.Host == "gotify.test");
        Assert.Equal("/message", gotify.Url.AbsolutePath);
        Assert.DoesNotContain("tok123", gotify.Url.ToString());
        Assert.Equal("tok123", gotify.Req.Headers.GetValues("X-Gotify-Key").Single());
    }

    [Theory]
    [InlineData("https://g.test", "/message", "")]
    [InlineData("https://g.test/sub/", "/sub/message", "")]
    [InlineData("https://g.test/sub/message", "/sub/message", "")]
    [InlineData("https://g.test/sub?x=1", "/sub/message", "?x=1")]
    public async Task Gotify_Url_AppendsMessageToPathOnly(string baseUrl, string path, string query)
    {
        var h = new RecordingHandler((_, _) => HttpStatusCode.OK);
        var opt = new NotificationOptions { GotifyBaseUrl = baseUrl, GotifyToken = "t" };
        await Make(opt, h).DispatchAsync(Fail(), default);
        var url = Assert.Single(h.Calls).Url;
        Assert.Equal(path, url.AbsolutePath);
        Assert.Equal(query, url.Query);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_ThenSucceeds()
    {
        var n = 0;
        var h = new RecordingHandler((_, _) => ++n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var opt = new NotificationOptions { DiscordWebhookUrl = "https://discord.test/hook" };
        await Make(opt, h).DispatchAsync(Fail(), default);
        Assert.Equal(3, h.Calls.Count);
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        var h = new RecordingHandler((_, _) => HttpStatusCode.NotFound);
        var opt = new NotificationOptions { DiscordWebhookUrl = "https://discord.test/hook" };
        await Make(opt, h).DispatchAsync(Fail(), default);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task OneProviderFailing_DoesNotBlockOther()
    {
        var h = new RecordingHandler((r, _) =>
            r.RequestUri!.Host == "discord.test" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        var opt = new NotificationOptions
        {
            DiscordWebhookUrl = "https://discord.test/hook",
            GotifyBaseUrl = "https://gotify.test",
            GotifyToken = "t"
        };
        await Make(opt, h).DispatchAsync(new JobNotification("abc", JobStatus.Succeeded, DateTimeOffset.UtcNow, null), default);
        Assert.Contains(h.Calls, c => c.Url.Host == "gotify.test");
    }
}

public sealed class NotificationOptionsValidatorTests
{
    private sealed class TestEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static ValidateOptionsResult Validate(NotificationOptions n) =>
        new SignRelayOptionsValidator(
                new TestEnv(),
                NullLogger<SignRelayOptionsValidator>.Instance)
            .Validate(null, new SignRelayOptions
            {
                CiToken = new string('a', 40),
                AgentToken = new string('b', 40),
                Notifications = n
            });

    [Fact] public void Empty_IsValid() => Assert.True(Validate(new()).Succeeded);

    [Fact]
    public void Valid_IsValid() => Assert.True(Validate(new()
    {
        DiscordWebhookUrl = "https://discord.test/x",
        GotifyBaseUrl = "https://g.test",
        GotifyToken = "t"
    }).Succeeded);

    [Fact] public void BadDiscordUrl_Fails() => Assert.True(Validate(new() { DiscordWebhookUrl = "nope" }).Failed);
    [Fact] public void FtpDiscordUrl_Fails() => Assert.True(Validate(new() { DiscordWebhookUrl = "ftp://x/y" }).Failed);
    [Fact] public void GotifyUrlWithoutToken_Fails() => Assert.True(Validate(new() { GotifyBaseUrl = "https://g.test" }).Failed);
    [Fact] public void GotifyTokenWithoutUrl_Fails() => Assert.True(Validate(new() { GotifyToken = "t" }).Failed);
}
