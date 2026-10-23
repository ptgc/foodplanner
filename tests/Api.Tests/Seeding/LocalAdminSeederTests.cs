using System.Net;
using Foodplanner.Api.Seeding;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Seeding;

public sealed class LocalAdminSeederTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string SeedPassword = "seed-password-1234";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AuthHost CreateHost(string email, string environment = "Development", string? password = SeedPassword) => AuthHost.Create(
        db.ConnectionString,
        new Dictionary<string, string?> { [LocalAdminSeeder.EmailKey] = email, [LocalAdminSeeder.PasswordKey] = password },
        environment);

    [Fact]
    public async Task Seed_creates_a_confirmed_admin_who_owns_Home_and_can_sign_in()
    {
        var email = AuthHost.NewEmail();
        await using var host = CreateHost(email);

        Assert.Equal(0, await LocalAdminSeeder.RunAsync(host.Services, Ct));

        var user = await host.FindUserAsync(email);
        Assert.True(user.EmailConfirmed);
        using var client = host.CreateClient();
        using var login = await client.LoginAsync(email, SeedPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var households = await client.GetAsync("/api/households");
        var body = await households.JsonAsync();
        var home = Assert.Single(body.EnumerateArray());
        Assert.Equal("Home", home.GetProperty("name").GetString());
        Assert.Equal("Owner", home.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Seeding_twice_creates_no_duplicates()
    {
        var email = AuthHost.NewEmail();
        await using var host = CreateHost(email);

        await LocalAdminSeeder.RunAsync(host.Services, Ct);
        await LocalAdminSeeder.RunAsync(host.Services, Ct);

        var user = await host.FindUserAsync(email);
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == email, Ct));
        Assert.Equal(1, await context.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]).CountAsync(m => m.UserId == user.Id, Ct));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task Seed_refuses_outside_Development_and_creates_nothing(string environment)
    {
        var email = AuthHost.NewEmail();
        await using var host = CreateHost(email, environment);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalAdminSeeder.RunAsync(host.Services, Ct));

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.Users.AnyAsync(u => u.Email == email, Ct));
    }

    [Fact]
    public async Task Seed_without_a_password_fails_and_creates_nothing()
    {
        var email = AuthHost.NewEmail();
        await using var host = CreateHost(email, password: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalAdminSeeder.RunAsync(host.Services, Ct));

        Assert.Null(await host.FindUserAsync(email));
    }
}
