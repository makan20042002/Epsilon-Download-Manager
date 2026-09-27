using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace MakanDownloadManager.Services;

public static class RetryPolicy
{
    public const int MaxAttempts = 4;
    const string RetryAfterKey = "Makan.RetryAfter";

    /// <summary>True when trying again could plausibly succeed (network drop, 5xx, 429), false for 404/403/disk-full/etc.</summary>
    public static bool IsTransient(Exception ex)
    {
        switch (ex)
        {
            case HttpRequestException { StatusCode: { } code }:
                var c = (int)code;
                return c is 408 or 425 or 429 or 500 or 502 or 503 or 504;
            case AuthenticationException:
            case InvalidDataException:
            case OperationCanceledException:
                return false;
            case HttpRequestException:
            case TimeoutException:
                return ex.InnerException is null || IsTransient(ex.InnerException);
            case IOException io:
                return !IsDiskProblem(io);
        }
        return ex.InnerException is not null && IsTransient(ex.InnerException);
    }

    /// <summary>Server asked us to slow down (429 / 503): callers should reduce parallel connections.</summary>
    public static bool IsThrottle(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable };

    static bool IsDiskProblem(IOException ex)
    {
        // Windows: ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39); Linux ENOSPC surfaces as HResult 28.
        var code = ex.HResult & 0xFFFF;
        return code is 112 or 39 or 28;
    }

    public static TimeSpan DelayForAttempt(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, Math.Max(0, attempt)));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Backoff that honours a server-supplied Retry-After (capped at 60 s).</summary>
    public static TimeSpan DelayFor(Exception ex, int attempt)
    {
        if (ex.Data[RetryAfterKey] is TimeSpan hinted && hinted > TimeSpan.Zero)
            return hinted > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : hinted;
        return DelayForAttempt(attempt);
    }

    public static HttpRequestException StatusError(HttpResponseMessage response)
    {
        var ex = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), null, response.StatusCode);
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta) ex.Data[RetryAfterKey] = delta;
        else if (ra?.Date is { } date) ex.Data[RetryAfterKey] = date - DateTimeOffset.UtcNow;
        return ex;
    }
}
