using System.Net;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Api.Tests.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Households;

/// <summary>
/// Adds one test-only endpoint per policy under <c>/api/households/{householdId}/test/...</c>, mapped on the same
/// <see cref="HouseholdRoutes.MapHouseholdScopedGroup"/> real features use. Only test hosts install it.
/// </summary>
internal sealed class PolicyTestEndpointsStartupFilter : IStartupFilter
{
    private IEndpointRouteBuilder? _routes;

    /// <summary>Every endpoint routing serves, including the ones this filter added.</summary>
    public IReadOnlyList<Endpoint> Endpoints => new CompositeEndpointDataSource(_routes!.DataSources).Endpoints;

    // UseRouting publishes the endpoint route builder under this key.
    private const string GlobalEndpointRouteBuilderKey = "__EndpointRouteBuilder";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        var routes = _routes = (IEndpointRouteBuilder)app.Properties[GlobalEndpointRouteBuilderKey]!;
        var test = routes.MapApiRoot().MapHouseholdScopedGroup().MapGroup("/test");
        test.MapGet("/member", () => Results.Ok()).RequireAuthorization(Policies.HouseholdMember);
        test.MapGet("/adult", () => Results.Ok()).RequireAuthorization(Policies.HouseholdAdult);
        // Deliberately no policy of its own: the group must still demand a membership.
        test.MapGet("/unprotected", () => Results.Ok());
        test.MapGet("/owner", () => Results.Ok()).RequireAuthorization(Policies.HouseholdOwner);
    };
}

internal static class HouseholdHost
{
    public static AuthHost Create(string connectionString, params (string Key, string Value)[] settings) =>
        AuthHost.Create(
            connectionString,
            settings.ToDictionary(s => s.Key, s => (string?)s.Value),
            configureServices: services => services.AddSingleton<IStartupFilter, PolicyTestEndpointsStartupFilter>());
}

internal sealed record SignedInUser(AppUserRef User, ApiClient Client);

internal sealed record AppUserRef(Guid Id, string Email);

internal static class HouseholdScenario
{
    public static async Task<SignedInUser> SignInAsync(this AuthHost host, string? remoteIp = null)
    {
        var email = AuthHost.NewEmail();
        var user = await host.CreateUserAsync(email);
        var client = host.CreateClient(remoteIp);
        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return new SignedInUser(new AppUserRef(user.Id, email), client);
    }

    public static async Task<Guid> CreateHouseholdAsync(this ApiClient client, string name = "The Smiths")
    {
        using var response = await client.PostAsync("/api/households", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static async Task<string> CreateInviteAsync(this ApiClient client, Guid householdId, string role)
    {
        using var response = await client.PostAsync($"/api/households/{householdId}/invites", new { role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("token").GetString()!;
    }

    public static Task<HttpResponseMessage> AcceptAsync(this ApiClient client, string token) =>
        client.PostAsync($"/api/invites/{token}/accept");

    /// <summary>Creates a household owned by <paramref name="owner"/> and a user who joined it with <paramref name="role"/>.</summary>
    public static async Task<SignedInUser> JoinAsync(this AuthHost host, SignedInUser owner, Guid householdId, string role)
    {
        var member = await host.SignInAsync();
        var token = await owner.Client.CreateInviteAsync(householdId, role);
        using var accepted = await member.Client.AcceptAsync(token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return member;
    }

    public static async Task<List<(Guid Id, string Name, string Role)>> ListHouseholdsAsync(this ApiClient client)
    {
        using var response = await client.GetAsync("/api/households");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        return [.. body.EnumerateArray().Select(h => (h.GetProperty("id").GetGuid(), h.GetProperty("name").GetString()!, h.GetProperty("role").GetString()!))];
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The problem body without per-request fields, for comparing two responses.</summary>
    public static async Task<string> ProblemShapeAsync(this HttpResponseMessage response)
    {
        var body = await response.JsonAsync();
        var fields = body.EnumerateObject().Where(p => p.Name is "type" or "title" or "status" or "detail").Select(p => $"{p.Name}={p.Value}");
        return string.Join("|", fields);
    }
}
