using System.Net;

namespace Foodplanner.Infrastructure.Import;

/// <summary>Resolves a host name to addresses. A seam so tests can answer without DNS; production uses <see cref="DnsHostResolver"/>.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// What the fetcher may connect to. Production uses <see cref="PublicInternetPolicy"/> only (no setting changes it); a
/// test host replaces the registration to reach a local test server.
/// </summary>
public interface IFetchPolicy
{
    bool IsPortAllowed(int port);

    bool IsAddressAllowed(IPAddress address);
}

/// <summary>Public addresses (<see cref="IpAddressRules"/>) on the web ports 80 and 443 only.</summary>
public sealed class PublicInternetPolicy : IFetchPolicy
{
    public bool IsPortAllowed(int port) => port is 80 or 443;

    public bool IsAddressAllowed(IPAddress address) => IpAddressRules.IsPublic(address);
}

public enum FetchFailure
{
    /// <summary>Not an http(s) URL on an allowed port, or has credentials, or is too long.</summary>
    InvalidUrl,

    /// <summary>The host resolves to (or redirects to) an address that may not be fetched.</summary>
    BlockedAddress,

    TooManyRedirects,

    /// <summary>The server answered with an error status (<see cref="FetchResult.StatusCode"/>).</summary>
    HttpStatus,

    UnsupportedContentType,

    TooLarge,

    Timeout,

    /// <summary>The host was not found, refused, reset, or sent something that is not HTTP.</summary>
    NetworkError,

    /// <summary>Too many fetches are running at once (a small global cap); try again shortly.</summary>
    Busy,
}

/// <summary>Success has <see cref="Content"/>; otherwise <see cref="Failure"/> says why. Never contains response headers or cookies.</summary>
public sealed record FetchResult(FetchFailure? Failure, string? Content, string? ContentType, Uri? FinalUrl, int? StatusCode)
{
    public bool Succeeded => Failure is null;

    public static FetchResult Ok(Uri finalUrl, string content, string contentType) => new(null, content, contentType, finalUrl, null);

    public static FetchResult Fail(FetchFailure failure, int? statusCode = null) => new(failure, null, null, null, statusCode);
}

/// <summary>Fetches a web page for recipe import. The only outbound HTTP the import does; see <see cref="SafeWebPageFetcher"/>.</summary>
public interface IWebPageFetcher
{
    Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Limits of the import fetch (section <c>RecipeImport</c>). They tighten or loosen size and time only; the address rules are not configurable.</summary>
public sealed class RecipeImportOptions
{
    public const string Section = "RecipeImport";

    public int MaxBytes { get; set; } = 2 * 1024 * 1024;

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxRedirects { get; set; } = 3;

    /// <summary>Fetches running at once in this process (not across replicas).</summary>
    public int MaxConcurrentFetches { get; set; } = 4;
}

/// <summary>Thrown inside the connect callback when the target address (or port) is refused. Mapped to <see cref="FetchFailure.BlockedAddress"/>.</summary>
public sealed class BlockedAddressException(string host) : IOException($"Connecting to '{host}' is not allowed.");
