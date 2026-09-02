using System.Text.Json;
using FastEndpoints;
using SignRelay.Contracts;
using SignRelay.Server.Api;
using SignRelay.Server.Auth;
using SignRelay.Server.Services;

namespace SignRelay.Server.Api.Ci;

public sealed class GetJobEventsEndpoint : EndpointWithoutRequest
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly JobService _jobs;
    private readonly JobEventHub _hub;
    private readonly ILogger<GetJobEventsEndpoint> _log;

    public GetJobEventsEndpoint(JobService jobs, JobEventHub hub, ILogger<GetJobEventsEndpoint> log)
    {
        _jobs = jobs;
        _hub = hub;
        _log = log;
    }

    public override void Configure()
    {
        Get($"{SignRelay.Contracts.ApiRoutes.Prefix}/jobs/{{id}}/events");
        AuthSchemes(SignRelayAuthenticationHandler.SchemeName);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!JobRoute.TryBind(Route<string>("id"), out var id))
        {
            AddError("Job id is invalid.");
            ServerHttpError.Log(_log, HttpContext, 400, "Job id is invalid.");
            await Send.ErrorsAsync(400, ct).ConfigureAwait(false);
            return;
        }

        if (!JobAccess.CanAccessJob(User, id))
        {
            ServerHttpError.Log(_log, HttpContext, 401, "Not authorized for this job.");
            await Send.UnauthorizedAsync(ct).ConfigureAwait(false);
            return;
        }

        // Subscribe BEFORE reading current state to eliminate the race where a transition
        // occurs between the DB read and the channel subscribe.
        var subscription = _hub.Subscribe(id);
        try
        {
            var job = await _jobs.GetJobAsync(id, ct).ConfigureAwait(false);
            if (job is null)
            {
                subscription.Dispose();
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "text/event-stream";
            HttpContext.Response.Headers.CacheControl = "no-cache";
            HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

            // Emit the current snapshot first
            await EmitAsync(_jobs.ToPayload(job), ct).ConfigureAwait(false);
            if (IsTerminal(job.Status))
                return;

            // Drain any events that were buffered while we were fetching (the subscribe-before-read fix)
            // then continue reading live events
            await foreach (var ev in subscription.Reader.ReadAllAsync(ct))
            {
                await EmitAsync(ev, ct).ConfigureAwait(false);
                if (ev.Type == "done")
                    break;
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private static bool IsTerminal(JobStatus s) =>
        s is JobStatus.Succeeded or JobStatus.Failed or JobStatus.TimedOut;

    private async Task EmitAsync(JobEventPayload payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        await HttpContext.Response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await HttpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
