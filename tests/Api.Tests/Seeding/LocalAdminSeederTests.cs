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

    private AuthHost CreateHost(string email, string environment = "Development", string? password = SeedPassword, string? memberEmail = null, string? demoEmail = null, string? demoSecret = null) => AuthHost.Create(
        db.ConnectionString,
        new Dictionary<string, string?>
        {
            [LocalAdminSeeder.EmailKey] = email,
            [LocalAdminSeeder.PasswordKey] = password,
            [LocalAdminSeeder.MemberEmailKey] = memberEmail,
            [LocalAdminSeeder.MemberPasswordKey] = memberEmail is null ? null : SeedPassword,
            [LocalAdminSeeder.DemoEmailKey] = demoEmail,
            [LocalAdminSeeder.DemoPasswordKey] = demoSecret,
        },
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

    [Fact]
    public async Task Seed_creates_a_confirmed_member_without_any_household_and_seeding_twice_adds_no_duplicate()
    {
        var admin = AuthHost.NewEmail();
        var member = AuthHost.NewEmail();
        await using var host = CreateHost(admin, memberEmail: member);

        await LocalAdminSeeder.RunAsync(host.Services, Ct);
        await LocalAdminSeeder.RunAsync(host.Services, Ct);

        var user = await host.FindUserAsync(member);
        Assert.True(user.EmailConfirmed);
        using var client = host.CreateClient();
        using var login = await client.LoginAsync(member, SeedPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var households = await client.GetAsync("/api/households");
        Assert.Empty((await households.JsonAsync()).EnumerateArray());
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == member, Ct));
        Assert.False(await context.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AnyAsync(m => m.UserId == user.Id, Ct));
    }

    [Fact]
    public async Task Seed_refuses_outside_Development_and_creates_no_member()
    {
        var member = AuthHost.NewEmail();
        await using var host = CreateHost(AuthHost.NewEmail(), "Production", memberEmail: member);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalAdminSeeder.RunAsync(host.Services, Ct));

        Assert.Null(await host.FindUserAsync(member));
    }

    [Fact]
    public async Task Seeding_twice_gives_one_demo_user_owning_one_demo_household_and_leaves_Home_and_member_alone()
    {
        var admin = AuthHost.NewEmail();
        var member = AuthHost.NewEmail();
        var demoEmail = AuthHost.NewEmail();
        await using var host = CreateHost(admin, memberEmail: member, demoEmail: demoEmail, demoSecret: SeedPassword);

        await LocalAdminSeeder.RunAsync(host.Services, Ct);
        await LocalAdminSeeder.RunAsync(host.Services, Ct);

        var demo = await host.FindUserAsync(demoEmail);
        Assert.True(demo.EmailConfirmed);
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == demoEmail, Ct));
        var memberships = context.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]);
        Assert.Equal(1, await memberships.CountAsync(m => m.UserId == demo.Id, Ct));
        using var client = host.CreateClient();
        using var login = await client.LoginAsync(demoEmail, SeedPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        using var households = await client.GetAsync("/api/households");
        var home = Assert.Single((await households.JsonAsync()).EnumerateArray());
        Assert.Equal(LocalAdminSeeder.DemoHouseholdName, home.GetProperty("name").GetString());
        Assert.Equal("Owner", home.GetProperty("role").GetString());
        var adminUser = await host.FindUserAsync(admin);
        Assert.Equal(1, await memberships.CountAsync(m => m.UserId == adminUser.Id, Ct));
        var memberUser = await host.FindUserAsync(member);
        Assert.False(await memberships.AnyAsync(m => m.UserId == memberUser.Id, Ct));
    }

    [Fact]
    public async Task Seed_makes_home_unlimited()
    {
        var admin = AuthHost.NewEmail();
        var demoEmail = AuthHost.NewEmail();
        await using var host = CreateHost(admin, demoEmail: demoEmail, demoSecret: SeedPassword);

        await LocalAdminSeeder.RunAsync(host.Services, Ct);
        await LocalAdminSeeder.RunAsync(host.Services, Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memberships = context.Memberships.IgnoreQueryFilters([AppDbContext.TenantFilterName]);
        var adminUser = await host.FindUserAsync(admin);
        var demoUser = await host.FindUserAsync(demoEmail);
        var homeId = (await memberships.SingleAsync(m => m.UserId == adminUser.Id, Ct)).HouseholdId;
        var demoId = (await memberships.SingleAsync(m => m.UserId == demoUser.Id, Ct)).HouseholdId;
        var plans = context.HouseholdPlans.IgnoreQueryFilters([AppDbContext.TenantFilterName]).AsNoTracking();

        var plan = await plans.SingleAsync(p => p.HouseholdId == homeId, Ct); // twice seeded, still one row
        Assert.Equal("Unlimited", plan.Name);
        Assert.True(plan.Limits.PhotoImportsPerDay.IsUnlimited);
        Assert.True(plan.Limits.LineHelpPerDay.IsUnlimited);
        Assert.True(plan.Limits.LineHelpPerMonth.IsUnlimited);
        Assert.False(await plans.AnyAsync(p => p.HouseholdId == demoId, Ct)); // the demo household keeps the default plan
    }

    [Fact]
    public async Task Seed_does_not_add_a_plan_outside_Development()
    {
        var admin = AuthHost.NewEmail();
        await using var host = CreateHost(admin, "Production");
        var before = await CountPlansAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalAdminSeeder.RunAsync(host.Services, Ct));

        Assert.Equal(before, await CountPlansAsync(host));
    }

    private static async Task<int> CountPlansAsync(AuthHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().HouseholdPlans.IgnoreQueryFilters([AppDbContext.TenantFilterName]).CountAsync(Ct);
    }

    [Fact]
    public async Task Demo_user_uses_the_default_address_when_none_is_configured()
    {
        await using var host = CreateHost(AuthHost.NewEmail(), demoSecret: SeedPassword);

        await LocalAdminSeeder.RunAsync(host.Services, Ct);

        Assert.EndsWith("@example.test", LocalAdminSeeder.DefaultDemoEmail);
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await context.Users.AnyAsync(u => u.Email == LocalAdminSeeder.DefaultDemoEmail, Ct));
    }

    [Fact]
    public async Task Seed_refuses_outside_Development_and_creates_no_demo_user()
    {
        var demoEmail = AuthHost.NewEmail();
        await using var host = CreateHost(AuthHost.NewEmail(), "Production", demoEmail: demoEmail, demoSecret: SeedPassword);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalAdminSeeder.RunAsync(host.Services, Ct));

        Assert.Null(await host.FindUserAsync(demoEmail));
    }
}
