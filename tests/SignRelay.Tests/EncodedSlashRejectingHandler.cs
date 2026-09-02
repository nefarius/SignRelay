using System.Net;

namespace SignRelay.Tests;

/// <summary>
/// Mimics Traefik encoded-character rejection: any path containing <c>%2F</c> returns 400
/// with an empty body, never reaching the application.
/// </summary>
internal sealed class EncodedSlashRejectingHandler : DelegatingHandler
{
    public EncodedSlashRejectingHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    public EncodedSlashRejectingHandler()
        : base(new HttpClientHandler())
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = request.RequestUri?.OriginalString ?? "";
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (raw.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2F", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request",
                Content = new ByteArrayContent([]),
                RequestMessage = request
            });
        }

        return base.SendAsync(request, cancellationToken);
    }
}
