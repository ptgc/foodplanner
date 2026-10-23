using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Api.Seeding;

/// <summary>
/// T-029: creates the local admin (a confirmed user, Owner of household "Home") from <c>Seed:AdminEmail</c> and
/// <c>Seed:AdminPassword</c> (user secrets). Run from the host with <c>dotnet run --project src/Api -- seed-admin</c>
/// (scripts/local-seed). Development only: it refuses in any other environment, and Program never runs it on its own.
/// Idempotent. Uses UserManager (configured hasher and password rules) and ITenantResolution (household + Owner).
/// </summary>
public static class LocalAdminSeeder
{
    public const string Command = "seed-admin";
    public const string HouseholdName = "Home";
    public const string EmailKey = "Seed:AdminEmail";
    public const string PasswordKey = "Seed:AdminPassword";

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

        var memberships = await resolution.GetMembershipsForUser(user.Id, cancellationToken);
        if (!memberships.Any(m => m.Name == HouseholdName && m.Role == HouseholdRole.Owner))
        {
            await resolution.CreateHouseholdWithOwner(HouseholdName, user.Id, cancellationToken);
        }

        return 0;
    }

    // Error codes only, so the password can never end up in a message.
    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Seeding the admin failed: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
