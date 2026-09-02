using System.Net;
using System.Net.Http.Headers;

namespace SignRelay.Contracts;

/// <summary>
/// Bounded retries for replay-safe file transfers (GET download / reconstructable POST upload).
/// </summary>
public static class HttpTransfer
{
    public const int MaxAttempts = 3;

    public static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError;

    public static bool IsRetryableException(Exception ex) =>
        ex is HttpRequestException or IOException;

    public static TimeSpan DelayForAttempt(int attemptZeroBased, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : ra;

        var ms = 200 * (1 << attemptZeroBased);
        return TimeSpan.FromMilliseconds(ms);
    }

    public static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        var ra = headers.RetryAfter;
        if (ra is null)
            return null;
        if (ra.Delta is { } delta)
            return delta;
        if (ra.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken ct,
        Action<string>? onFailure = null,
        TimeProvider? timeProvider = null,
        Func<int, TimeSpan?, TimeSpan>? delayForAttempt = null)
    {
        timeProvider ??= TimeProvider.System;
        delayForAttempt ??= DelayForAttempt;

        HttpResponseMessage? lastResponse = null;
        Exception? lastTransport = null;
        string? lastDetails = null;
        HttpStatusCode? lastStatus = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lastResponse?.Dispose();
            lastResponse = null;
            lastTransport = null;

            HttpRequestMessage? request = null;
            try
            {
                request = requestFactory();
                lastResponse = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                request = null;

                if (lastResponse.IsSuccessStatusCode)
                    return lastResponse;

                lastStatus = lastResponse.StatusCode;
                lastDetails = await HttpFailureDetails.FromResponseAsync(
                        operation, attempt, MaxAttempts, lastResponse, ct)
                    .ConfigureAwait(false);
                onFailure?.Invoke(lastDetails);

                var retryable = attempt < MaxAttempts && IsRetryableStatus(lastResponse.StatusCode);
                if (!retryable)
                    throw new HttpRequestException(lastDetails, null, lastResponse.StatusCode);

                var delay = delayForAttempt(attempt - 1, ParseRetryAfter(lastResponse.Headers));
                lastResponse.Dispose();
                lastResponse = null;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                request?.Dispose();
                lastResponse?.Dispose();
                throw;
            }
            catch (HttpRequestException) when (attempt >= MaxAttempts || lastStatus is { } st && !IsRetryableStatus(st))
            {
                request?.Dispose();
                lastResponse?.Dispose();
                throw;
            }
            catch (Exception ex) when (IsRetryableException(ex) && attempt < MaxAttempts)
            {
                request?.Dispose();
                lastResponse?.Dispose();
                lastResponse = null;
                lastTransport = ex;
                lastDetails = $"HTTP failure: {operation} attempt {attempt}/{MaxAttempts}\nTransport error: {ex.Message}";
                onFailure?.Invoke(lastDetails);
                var delay = delayForAttempt(attempt - 1, null);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
            }
            catch
            {
                request?.Dispose();
                lastResponse?.Dispose();
                throw;
            }
        }

        throw lastTransport ?? new HttpRequestException(lastDetails ?? $"HTTP failure: {operation} exhausted retries.");
    }

    public static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string destPath,
        string operation,
        CancellationToken ct,
        Action<string>? onFailure = null,
        TimeProvider? timeProvider = null,
        Func<int, TimeSpan?, TimeSpan>? delayForAttempt = null)
    {
        var tmp = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await SendWithRetryAsync(
                    http,
                    () => new HttpRequestMessage(HttpMethod.Get, url),
                    operation,
                    ct,
                    onFailure,
                    timeProvider,
                    delayForAttempt)
                .ConfigureAwait(false);

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using (var fs = File.Create(tmp))
            {
                await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            File.Move(tmp, destPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // best-effort
            }

            throw;
        }
    }
}
