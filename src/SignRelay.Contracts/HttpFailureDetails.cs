using System.Net.Http.Headers;
using System.Text;

namespace SignRelay.Contracts;

/// <summary>
/// Canonical HTTP failure text for logs, job ErrorMessage, and SSE events.
/// </summary>
public static class HttpFailureDetails
{
    public const int PersistMaxChars = 16_000;
    public const string TruncationMarker = "\n[truncated]";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Auth-Token"
    };

    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);

    public static async Task<string> FromResponseAsync(
        string operation,
        int attempt,
        int maxAttempts,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        string? body = null;
        string? bodyReadError = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bodyReadError = ex.Message;
        }

        var request = response.RequestMessage;
        var method = request?.Method.Method ?? "(unknown)";
        var path = RequestPath(request);

        return Format(
            operation,
            attempt,
            maxAttempts,
            method,
            path,
            (int)response.StatusCode,
            response.ReasonPhrase,
            SafeResponseHeaders(response.Headers, response.Content.Headers),
            body,
            bodyReadError);
    }

    public static string Format(
        string operation,
        int attempt,
        int maxAttempts,
        string method,
        string path,
        int? statusCode,
        string? reasonPhrase,
        IReadOnlyList<string>? safeHeaders,
        string? body,
        string? bodyReadError)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP failure: ").Append(operation);
        sb.Append(" attempt ").Append(attempt).Append('/').Append(maxAttempts);
        sb.AppendLine();
        sb.Append(method).Append(' ').Append(path).Append(" → ");
        if (statusCode is { } code)
        {
            sb.Append(code);
            if (!string.IsNullOrEmpty(reasonPhrase))
                sb.Append(' ').Append(reasonPhrase);
        }
        else
        {
            sb.Append("(no status)");
        }

        sb.AppendLine();
        if (safeHeaders is { Count: > 0 })
        {
            sb.Append("Headers: ");
            sb.AppendJoin("; ", safeHeaders);
            sb.AppendLine();
        }

        if (bodyReadError is not null)
        {
            sb.Append("Body read failed: ").Append(bodyReadError);
            if (body is { Length: > 0 })
            {
                sb.AppendLine();
                sb.Append("Body:").AppendLine().Append(body);
            }
        }
        else if (string.IsNullOrEmpty(body))
        {
            sb.Append("Body: (empty)");
        }
        else
        {
            sb.Append("Body:").AppendLine().Append(body);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Persist as much as fits in <see cref="PersistMaxChars"/>, marking truncation explicitly.
    /// </summary>
    public static string Persist(string details)
    {
        if (details.Length <= PersistMaxChars)
            return details;

        var budget = PersistMaxChars - TruncationMarker.Length;
        if (budget < 0)
            return TruncationMarker;
        return details[..budget] + TruncationMarker;
    }

    public static IReadOnlyList<string> SafeResponseHeaders(HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        var list = new List<string>();
        AppendHeaders(list, headers);
        AppendHeaders(list, contentHeaders);
        return list;
    }

    private static void AppendHeaders(List<string> list, HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            if (IsSensitiveHeader(header.Key))
                continue;
            list.Add($"{header.Key}: {string.Join(", ", header.Value)}");
        }
    }

    private static string RequestPath(HttpRequestMessage? request)
    {
        if (request?.RequestUri is not { } uri)
            return "(unknown)";

        if (uri.IsAbsoluteUri)
            return uri.PathAndQuery;

        return uri.OriginalString;
    }
}
