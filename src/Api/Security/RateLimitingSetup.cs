using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Foodplanner.Api.Problems;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Security;

public static class RateLimitPolicies
{
    /// <summary>Login, register, confirm-email: by client IP (login also by email, see <see cref="LoginEmailRateLimiter"/>).</summary>
    public const string Auth = "auth";

    /// <summary>Attached by T-011: by client IP and user.</summary>
    public const string InviteAccept = "invite-accept";

    /// <summary>Household data export (T-048): by user. The export reads every table of the household.</summary>
    public const string HouseholdExport = "household-export";

    /// <summary>Attached by T-010: by client IP.</summary>
    public const string ExternalSignin = "external-signin";
}

public sealed class LimitOptions
{
    public int PermitLimit { get; set; } = 20;

    public int WindowSeconds { get; set; } = 60;

    internal FixedWindowRateLimiterOptions ToFixedWindow() => new()
    {
        PermitLimit = Math.Max(1, PermitLimit),
        Window = TimeSpan.FromSeconds(Math.Max(1, WindowSeconds)),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

/// <summary>Section <c>RateLimiting</c>. Tuning and edge limits belong to T-047.</summary>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";

    public LimitOptions Auth { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    public LimitOptions LoginByEmail { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    public LimitOptions InviteAccept { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    public LimitOptions ExternalSignin { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };

    public LimitOptions Export { get; set; } = new() { PermitLimit = 3, WindowSeconds = 60 };

    /// <summary>Recipe imports from a URL (T-042), per user: 10 per 10 minutes. See <see cref="UrlImportRateLimiter"/>.</summary>
    public LimitOptions UrlImport { get; set; } = new() { PermitLimit = 10, WindowSeconds = 600 };
}

/// <summary>
/// Per-email limit for login. The email is in the request body, which the rate limiter middleware does not read, so
/// the login handler asks this limiter once it has bound the body.
/// </summary>
public sealed class LoginEmailRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _window;

    public LoginEmailRateLimiter(IOptions<RateLimitOptions> options)
    {
        var limit = options.Value.LoginByEmail;
        _window = TimeSpan.FromSeconds(Math.Max(1, limit.WindowSeconds));
        _limiter = PartitionedRateLimiter.Create<string, string>(
            email => RateLimitPartition.GetFixedWindowLimiter(email, _ => limit.ToFixedWindow()));
    }

    /// <summary>Returns true when allowed; otherwise <paramref name="retryAfter"/> says when to retry.</summary>
    public bool TryAcquire(string normalizedEmail, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(normalizedEmail);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _window;
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

public static class RateLimitingSetup
{
    public const string TrustedNetworksSetting = "ForwardedHeaders:TrustedNetworks";

    public static IServiceCollection AddFoodplannerRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.Section);
        services.AddSingleton<LoginEmailRateLimiter>();
        services.AddSingleton<UrlImportRateLimiter>();
        services.AddRateLimiter(_ => { });
        // Configured through options so the limits see the final configuration (tests override them).
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, options) =>
        {
            var limits = options.Value;
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(limits.Auth.WindowSeconds);
                await WriteRateLimitedAsync(context.HttpContext, retryAfter);
            };
            limiter.AddPolicy(RateLimitPolicies.Auth, http =>
                RateLimitPartition.GetFixedWindowLimiter($"ip:{ClientIp(http)}", _ => limits.Auth.ToFixedWindow()));
            limiter.AddPolicy(RateLimitPolicies.InviteAccept, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    $"ip:{ClientIp(http)}|user:{http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous"}", _ => limits.InviteAccept.ToFixedWindow()));
            limiter.AddPolicy(RateLimitPolicies.HouseholdExport, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    $"user:{http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? $"ip:{ClientIp(http)}"}", _ => limits.Export.ToFixedWindow()));
            limiter.AddPolicy(RateLimitPolicies.ExternalSignin, http =>
                RateLimitPartition.GetFixedWindowLimiter($"ip:{ClientIp(http)}", _ => limits.ExternalSignin.ToFixedWindow()));
        });

        // Forwarded headers are honoured only from the configured proxy networks (CIDR list). With none configured the
        // middleware is not added at all, because an empty list would mean "trust everyone".
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((forwarded, configuration) =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var cidr in TrustedNetworks(configuration))
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });
        return services;
    }

    public static string[] TrustedNetworks(IConfiguration configuration) =>
        configuration.GetSection(TrustedNetworksSetting).Get<string[]>()?.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];

    public static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static Task WriteRateLimitedAsync(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return AppProblems.WriteAsync(
            http,
            StatusCodes.Status429TooManyRequests,
            AppProblems.RateLimited,
            "Too many requests",
            $"Try again in {seconds} seconds.");
    }
}
