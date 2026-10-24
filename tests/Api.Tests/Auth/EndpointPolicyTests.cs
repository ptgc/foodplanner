using Foodplanner.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Auth;

/// <summary>Whole-surface checks over the real endpoint table. No database is touched.</summary>
public sealed class EndpointPolicyTests
{
    private static readonly string[] UnsafeMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private static List<RouteEndpoint> Endpoints(string environment)
    {
        var host = AuthHost.Create(connectionString: string.Empty, environment: environment);
        try
        {
            return [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void All_unsafe_endpoints_validate_antiforgery(string environment)
    {
        var unsafeEndpoints = Endpoints(environment).Where(e => Methods(e).Any(m => UnsafeMethods.Contains(m, StringComparer.OrdinalIgnoreCase))).ToList();

        // Not vacuous: the auth endpoints exist.
        Assert.True(unsafeEndpoints.Count >= 4, $"found {unsafeEndpoints.Count} unsafe endpoints");
        var outside = unsafeEndpoints.Where(e => e.Metadata.GetMetadata<AntiforgeryValidatedMetadata>() is null).Select(e => e.DisplayName).ToList();
        Assert.Empty(outside);
        Assert.All(unsafeEndpoints, e => Assert.StartsWith("/api/", e.RoutePattern.RawText, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Every_hub_endpoint_requires_an_authenticated_user_and_none_is_anonymous(string environment)
    {
        // SignalR maps negotiate without an HTTP method, so All_unsafe_endpoints_validate_antiforgery does not see it. That
        // is deliberate (T-034): negotiate changes no state, needs the SameSite=Lax auth cookie, and HubOriginMiddleware
        // rejects a foreign Origin; see docs/domain.md, shopping list sync. This test pins what must hold instead.
        var hubEndpoints = Endpoints(environment).Where(e => e.RoutePattern.RawText?.StartsWith("/api/hubs/", StringComparison.Ordinal) == true).ToList();

        Assert.NotEmpty(hubEndpoints); // not vacuous: negotiate and the socket endpoint
        Assert.All(hubEndpoints, e =>
        {
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>());
        });
    }

    [Theory]
    [InlineData("Production", new string[0])]
    [InlineData("Development", new[] { "GET /openapi/{documentName}.json" })]
    public void Anonymous_endpoints_match_allow_list(string environment, string[] extraInEnvironment)
    {
        string[] allowList =
        [
            "GET /api/health",
            "GET /api/health/ready",
            "GET /api/auth/antiforgery",
            "POST /api/auth/register",
            "POST /api/auth/login",
            "POST /api/auth/confirm-email",
        ];

        var anonymous = Endpoints(environment)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(e => Methods(e).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(allowList.Concat(extraInEnvironment).Order(StringComparer.Ordinal).ToList(), anonymous);
    }
}
