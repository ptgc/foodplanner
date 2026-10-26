using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Security;

/// <summary>
/// Limits URL imports per user (T-042): each one makes the server fetch a third-party page. The URL is in the request body,
/// which the rate limiter middleware does not read, so the handler asks this limiter after it has bound the body (like the
/// login limiter). Text imports fetch nothing and are not counted.
/// </summary>
public sealed class UrlImportRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;
    private readonly TimeSpan _window;

    public UrlImportRateLimiter(IOptions<RateLimitOptions> options)
    {
        var limit = options.Value.UrlImport;
        _window = TimeSpan.FromSeconds(Math.Max(1, limit.WindowSeconds));
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(
            user => RateLimitPartition.GetFixedWindowLimiter(user, _ => limit.ToFixedWindow()));
    }

    /// <summary>Returns true when allowed; otherwise <paramref name="retryAfter"/> says when to retry.</summary>
    public bool TryAcquire(Guid userId, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _window;
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>The standard 429 ProblemDetails with Retry-After, as a result a handler can return.</summary>
public sealed class RetryAfterResult(TimeSpan retryAfter) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => RateLimitingSetup.WriteRateLimitedAsync(httpContext, retryAfter);
}
