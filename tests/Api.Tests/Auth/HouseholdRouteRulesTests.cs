using Foodplanner.Api.Authorization;
using Foodplanner.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Auth;

/// <summary>
/// Gap f. Everything under <c>/api/households/{householdId}/...</c> runs in that household (Q-004), and the household
/// context middleware only protects it together with a policy. An <c>AllowAnonymous</c> there would serve household data
/// to anyone, so no such endpoint may exist. No database is touched.
/// </summary>
public sealed class HouseholdRouteRulesTests
{
    private const string Prefix = "/api/households/{" + HouseholdRoutes.RouteParameter;

    internal static List<string> Violations(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => Problems(e).Select(p => $"{Methods(e)} {e.RoutePattern.RawText}: {p}"))];

    private static readonly string[] HouseholdPolicies = [Policies.HouseholdMember, Policies.HouseholdAdult, Policies.HouseholdOwner];

    private static IEnumerable<string> Problems(RouteEndpoint endpoint)
    {
        var raw = endpoint.RoutePattern.RawText ?? string.Empty;
        if (!raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (endpoint.RoutePattern.Parameters.Any(p => p.Name == HouseholdRoutes.RouteParameter))
            {
                yield return "has a householdId route parameter outside /api/households/{householdId}; the household context is only resolved there";
            }

            yield break;
        }

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            yield return "AllowAnonymous on a household-scoped route";
        }

        if (!endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is { } policy && HouseholdPolicies.Contains(policy)))
        {
            yield return "no HouseholdMember, HouseholdAdult or HouseholdOwner policy (a bare [Authorize] only proves a login, not a membership)";
        }

        if (endpoint.Metadata.GetMetadata<HouseholdScopedMetadata>() is null)
        {
            yield return "not mapped through MapHouseholdScopedGroup, so no household context is resolved";
        }
    }

    private static string Methods(RouteEndpoint endpoint) =>
        string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []);

    [Fact]
    public async Task No_real_household_scoped_endpoint_allows_anonymous_and_all_require_authorization()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.Contains(endpoints.OfType<RouteEndpoint>(), e => e.RoutePattern.RawText?.StartsWith(Prefix, StringComparison.Ordinal) == true); // not vacuous
        Assert.Empty(Violations(endpoints));
    }

    [Fact]
    public void An_anonymous_household_endpoint_is_reported()
    {
        var group = new FakeEndpoints();
        group.Add("/api/households/{householdId:guid}/recipes", new AllowAnonymousAttribute(), HouseholdScopedMetadata.Instance, new AuthorizeAttribute(Policies.HouseholdMember));
        group.Add("/api/households/{householdId:guid}/no-policy", HouseholdScopedMetadata.Instance);
        group.Add("/api/households/{householdId:guid}/unscoped", new AuthorizeAttribute(Policies.HouseholdMember));
        group.Add("/api/households/{householdId:guid}/bare-authorize", HouseholdScopedMetadata.Instance, new AuthorizeAttribute());
        group.Add("/api/households/{householdId:guid}/fine", HouseholdScopedMetadata.Instance, new AuthorizeAttribute(Policies.HouseholdAdult));
        group.Add("/api/other/{householdId:guid}/stray", new AuthorizeAttribute(Policies.HouseholdMember));
        group.Add("/api/health", new AllowAnonymousAttribute());

        var violations = Violations(group.Endpoints);

        Assert.Contains(violations, v => v.Contains("/recipes", StringComparison.Ordinal) && v.Contains("AllowAnonymous", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/no-policy", StringComparison.Ordinal) && v.Contains("policy", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/bare-authorize", StringComparison.Ordinal) && v.Contains("policy", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/api/other/", StringComparison.Ordinal) && v.Contains("outside", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("/unscoped", StringComparison.Ordinal) && v.Contains("MapHouseholdScopedGroup", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("/fine", StringComparison.Ordinal) || v.Contains("/api/health", StringComparison.Ordinal));
    }

    private sealed class FakeEndpoints
    {
        private readonly List<Endpoint> _endpoints = [];

        public IReadOnlyList<Endpoint> Endpoints => _endpoints;

        public void Add(string pattern, params object[] metadata)
        {
            var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0);
            builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
            foreach (var item in metadata)
            {
                builder.Metadata.Add(item);
            }

            _endpoints.Add(builder.Build());
        }
    }
}
