using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SignRelay.Contracts;
using SignRelay.Server.Options;

namespace SignRelay.Server.Services;

/// <summary>A terminal signing outcome to announce.</summary>
public sealed record JobNotification(string JobId, JobStatus Status, DateTimeOffset CompletedUtc, string? Error)
{
    public bool IsSuccess => Status == JobStatus.Succeeded;
}

public interface IJobNotificationQueue
{
    /// <summary>Non-blocking; never throws. Drops the notification if the queue is full or disabled.</summary>
    void Enqueue(JobNotification notification);
}

/// <summary>Bounded in-memory queue drained by <see cref="JobNotificationDispatcher"/>.</summary>
public sealed class JobNotificationQueue : IJobNotificationQueue
{
    private readonly Channel<JobNotification> _channel = Channel.CreateBounded<JobNotification>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    internal ChannelReader<JobNotification> Reader => _channel.Reader;

    public void Enqueue(JobNotification notification) => _channel.Writer.TryWrite(notification);
}

/// <summary>Sends Discord / Gotify messages best-effort; failures never affect jobs.</summary>
public sealed class JobNotificationDispatcher : BackgroundService
{
    public const string HttpClientName = "SignRelayNotifications";
    private const int MaxAttempts = 3;

    private readonly JobNotificationQueue _queue;
    private readonly IHttpClientFactory _http;
    private readonly NotificationOptions _opt;
    private readonly ILogger<JobNotificationDispatcher> _log;

    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public JobNotificationDispatcher(
        JobNotificationQueue queue,
        IHttpClientFactory http,
        IOptions<SignRelayOptions> options,
        ILogger<JobNotificationDispatcher> log)
    {
        _queue = queue;
        _http = http;
        _opt = options.Value.Notifications ?? new NotificationOptions();
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var n in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await DispatchAsync(n, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Notification dispatch for job {JobId} failed: {Type}", n.JobId, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task DispatchAsync(JobNotification n, CancellationToken ct)
    {
        var tasks = new List<Task>(2);
        if (_opt.DiscordEnabled)
            tasks.Add(SendWithRetryAsync("Discord", n, () => BuildDiscord(n), ct));
        if (_opt.GotifyEnabled)
            tasks.Add(SendWithRetryAsync("Gotify", n, () => BuildGotify(n), ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string Title(JobNotification n) => n.Status switch
    {
        JobStatus.Succeeded => "Signing succeeded",
        JobStatus.TimedOut => "Signing timed out",
        _ => "Signing failed"
    };

    private static string Body(JobNotification n)
    {
        var sb = new StringBuilder();
        sb.Append("Job ").Append(n.JobId).Append(" finished with status ").Append(n.Status)
            .Append(" at ").Append(n.CompletedUtc.UtcDateTime.ToString("u")).Append('.');
        if (!string.IsNullOrWhiteSpace(n.Error))
            sb.Append("\nError: ").Append(Truncate(n.Error, 1000));
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private HttpRequestMessage BuildDiscord(JobNotification n)
    {
        var color = n.IsSuccess ? 0x2ECC71 : 0xE74C3C;
        var payload = new
        {
            username = "SignRelay",
            embeds = new[]
            {
                new
                {
                    title = Title(n),
                    description = Truncate(Body(n), 4000),
                    color,
                    timestamp = n.CompletedUtc.UtcDateTime.ToString("o")
                }
            }
        };
        return new HttpRequestMessage(HttpMethod.Post, _opt.DiscordWebhookUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    private HttpRequestMessage BuildGotify(JobNotification n)
    {
        var baseUrl = _opt.GotifyBaseUrl.TrimEnd('/') + "/message";
        var payload = new { title = Title(n), message = Body(n), priority = n.IsSuccess ? 4 : 8 };
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Gotify-Key", _opt.GotifyToken);
        return req;
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;

    private async Task SendWithRetryAsync(string provider, JobNotification n, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            string? failure;
            var transient = true;
            try
            {
                using var req = build();
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return;
                failure = $"HTTP {(int)resp.StatusCode}";
                transient = IsTransient(resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Exception messages from HttpClient can embed the request URL (a secret); log type only.
                failure = ex.GetType().Name;
            }

            if (!transient || attempt == MaxAttempts)
            {
                _log.LogWarning("{Provider} notification for job {JobId} failed after {Attempts} attempt(s): {Failure}",
                    provider, n.JobId, attempt, failure);
                return;
            }

            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }
}
