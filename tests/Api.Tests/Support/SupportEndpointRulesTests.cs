using Foodplanner.Api.Authorization;
using Foodplanner.Api.Support;
using Foodplanner.Api.Tests.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Support;

/// <summary>T-096: whole-surface checks over the real endpoint table. No database is touched.</summary>
public sealed class SupportEndpointRulesTests
{
    private const string SupportPrefix = "/api/support";
    private const string ScopedPrefix = "/api/support/households/{" + SupportRoutes.RouteParameter;

    private static async Task<List<RouteEndpoint>> SupportEndpointsAsync(string environment)
    {
        await using var host = AuthHost.Create(connectionString: string.Empty, environment: environment);
        return
        [
            .. host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText?.StartsWith(SupportPrefix, StringComparison.Ordinal) == true),
        ];
    }

    private static string[] Policy(RouteEndpoint endpoint) =>
        [.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).Select(p => p!)];

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Every_support_endpoint_is_policy_protected_and_declares_its_audit_action(string environment)
    {
        var endpoints = await SupportEndpointsAsync(environment);
        Assert.True(endpoints.Count >= 17, $"found {endpoints.Count} support endpoints"); // not vacuous

        foreach (var endpoint in endpoints)
        {
            var name = endpoint.RoutePattern.RawText!;
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<SupportRouteMetadata>());
            Assert.Contains(Policies.SystemAdmin, Policy(endpoint));

            // Every one is audited, and the kind agrees with the policy: only the named fixes need a write session.
            var audited = endpoint.Metadata.GetMetadata<SupportAuditedMetadata>();
            Assert.True(audited is not null, $"{name} has no SupportAuditedMetadata");
            Assert.False(string.IsNullOrWhiteSpace(audited.Action));

            if (name.StartsWith(ScopedPrefix, StringComparison.Ordinal))
            {
                Assert.NotNull(endpoint.Metadata.GetMetadata<SupportScopedMetadata>());
                Assert.Contains(Policy(endpoint), p => p is Policies.SupportSessionRead or Policies.SupportSessionWrite);
                Assert.Equal(audited.Kind == SupportActionKind.Write, Policy(endpoint).Contains(Policies.SupportSessionWrite));
            }
            else
            {
                Assert.Null(endpoint.Metadata.GetMetadata<SupportScopedMetadata>());
                Assert.DoesNotContain(Policies.SupportSessionWrite, Policy(endpoint));
                Assert.NotEqual(SupportActionKind.Write, audited.Kind);
            }
        }

        // Nothing but support lives under the two trees, and no endpoint outside /api/support has the support metadata.
        var groups = endpoints.Select(e => e.RoutePattern.RawText!).Where(r => !r.StartsWith("/api/support/sessions", StringComparison.Ordinal)
            && !r.StartsWith(ScopedPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["/api/support/households", "/api/support/users"], groups);
    }

    [Fact]
    public async Task The_support_policies_include_full_session_and_admin_and_the_household_support_list_is_adult_only()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in new[] { Policies.SystemAdmin, Policies.SupportSessionRead, Policies.SupportSessionWrite })
        {
            var policy = await policies.GetPolicyAsync(name);
            Assert.NotNull(policy);
            Assert.Contains(policy.Requirements, r => r is FullSessionRequirement); // kid mode wins
            Assert.Contains(policy.Requirements, r => r is SystemAdminRequirement);
            Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        }

        Assert.DoesNotContain((await policies.GetPolicyAsync(Policies.SystemAdmin))!.Requirements, r => r is SupportSessionRequirement);
        Assert.Contains((await policies.GetPolicyAsync(Policies.SupportSessionRead))!.Requirements, r => r is SupportSessionRequirement { RequireWrite: false });
        Assert.Contains((await policies.GetPolicyAsync(Policies.SupportSessionWrite))!.Requirements, r => r is SupportSessionRequirement { RequireWrite: true });

        // What a household sees of support is an ordinary household route for Owner and Adult.
        var access = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/households/{householdId:guid}/support-access");
        Assert.Contains(Policies.HouseholdAdult, Policy(access));
        Assert.Contains(Policies.HouseholdMember, Policy(access));
        Assert.DoesNotContain(Policies.SystemAdmin, Policy(access));
    }

    [Fact]
    public async Task Support_metadata_exists_only_under_api_support()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        var outside = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(SupportPrefix, StringComparison.Ordinal) != true)
            .Where(e => e.Metadata.GetMetadata<SupportRouteMetadata>() is not null
                || e.Metadata.GetMetadata<SupportScopedMetadata>() is not null
                || e.Metadata.GetMetadata<SupportAuditedMetadata>() is not null
                || Policy(e).Any(p => p is Policies.SystemAdmin or Policies.SupportSessionRead or Policies.SupportSessionWrite))
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.Empty(outside);
    }
}
