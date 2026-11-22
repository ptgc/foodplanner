using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Seeding;

/// <summary>
/// T-029: creates the local admin (a confirmed user, Owner of household "Home") from <c>Seed:AdminEmail</c> and
/// <c>Seed:AdminPassword</c> (user secrets). Run from the host with <c>dotnet run --project src/Api -- seed-admin</c>
/// (scripts/local-seed). Development only: it refuses in any other environment, and Program never runs it on its own.
/// Optionally (T-045, e2e invite flow) also creates a second confirmed user from <c>Seed:MemberEmail</c> and
/// <c>Seed:MemberPassword</c> who belongs to NO household: the tests invite them. Optionally (T-122) a demo user (Seed:DemoEmail, default havard.rikke@example.test, and Seed:DemoPassword) who owns household "Håvard og Rikke". Idempotent. Uses UserManager (configured hasher and password rules) and ITenantResolution (household + Owner).
/// </summary>
public static class LocalAdminSeeder
{
    public const string Command = "seed-admin";
    public const string HouseholdName = "Home";
    public const string EmailKey = "Seed:AdminEmail";
    public const string PasswordKey = "Seed:AdminPassword";
    public const string MemberEmailKey = "Seed:MemberEmail";
    public const string MemberPasswordKey = "Seed:MemberPassword";
    public const string DemoHouseholdName = "Håvard og Rikke";
    public const string DemoEmailKey = "Seed:DemoEmail";
    public const string DemoPasswordKey = "Seed:DemoPassword";
    // example.test is reserved (RFC 2606): it can never receive mail.
    public const string DefaultDemoEmail = "havard.rikke@example.test";

    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException($"Seeding the local admin only runs in Development (environment is {environment.EnvironmentName}).");
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var email = configuration[EmailKey];
        var password = configuration[PasswordKey];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException($"{EmailKey} and {PasswordKey} must be set (run scripts/local-secrets).");
        }

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var resolution = scope.ServiceProvider.GetRequiredService<ITenantResolution>();

        var user = await EnsureConfirmedUserAsync(users, email, password);

        var memberships = await resolution.GetMembershipsForUser(user.Id, cancellationToken);
        if (!memberships.Any(m => m.Name == HouseholdName && m.Role == HouseholdRole.Owner))
        {
            await resolution.CreateHouseholdWithOwner(HouseholdName, user.Id, cancellationToken);
        }

        // Optional second user for the e2e invite flow. Not added to any household here.
        var memberEmail = configuration[MemberEmailKey];
        var memberPw = configuration[MemberPasswordKey];
        if (!string.IsNullOrWhiteSpace(memberEmail) && !string.IsNullOrEmpty(memberPw))
        {
            await EnsureConfirmedUserAsync(users, memberEmail, memberPw);
        }

        // T-122: a second household with its own Owner, so two households can be seen locally (super admin, limits).
        var demoPw = configuration[DemoPasswordKey];
        if (!string.IsNullOrEmpty(demoPw))
        {
            var demoEmail = configuration[DemoEmailKey];
            if (string.IsNullOrWhiteSpace(demoEmail))
            {
                demoEmail = DefaultDemoEmail;
            }

            var demo = await EnsureConfirmedUserAsync(users, demoEmail, demoPw);
            var demoMemberships = await resolution.GetMembershipsForUser(demo.Id, cancellationToken);
            if (!demoMemberships.Any(m => m.Name == DemoHouseholdName && m.Role == HouseholdRole.Owner))
            {
                await resolution.CreateHouseholdWithOwner(DemoHouseholdName, demo.Id, cancellationToken);
            }
        }

        return 0;
    }

    private static async Task<AppUser> EnsureConfirmedUserAsync(UserManager<AppUser> users, string email, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = true };
            ThrowIfFailed(await users.CreateAsync(user, password));
        }
        else if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            ThrowIfFailed(await users.UpdateAsync(user));
        }

        return user;
    }

    // Error codes only, so the password can never end up in a message.
    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Seeding a local user failed: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
