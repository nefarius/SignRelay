namespace SignRelay.Server.Api;

internal static class ServerHttpError
{
    public static void Log(ILogger log, HttpContext ctx, int status, string? body)
    {
        log.LogWarning(
            "HTTP failure: {Method} {Path} → {Status}\nBody:\n{Body}",
            ctx.Request.Method,
            ctx.Request.Path.Value ?? "(unknown)",
            status,
            string.IsNullOrEmpty(body) ? "(empty)" : body);
    }
}
