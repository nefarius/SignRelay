using FastEndpoints;

namespace SignRelay.Server.Api;

public sealed class GetIndexEndpoint : EndpointWithoutRequest<ServerVersionResponse>
{
    public override void Configure()
    {
        Get("/");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(new ServerVersionResponse { Version = ServerVersion.Current }, ct)
            .ConfigureAwait(false);
    }
}
