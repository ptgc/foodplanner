using System.Globalization;
using System.Security.Claims;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

/// <summary>
/// When an account was last offered a set-password link, kept as an Identity user claim so no column is needed on
/// the user table (T-072). The value is unix seconds, zero padded so the database can compare it as text.
/// </summary>
internal static class RegistrationClaim
{
    public const string Type = "foodplanner:registered-at";

    public static string Value(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString("D12", CultureInfo.InvariantCulture);

    public static Claim Create(DateTimeOffset at) => new(Type, Value(at));

    /// <summary>Records <paramref name="at"/> as the registration time, replacing any earlier one.</summary>
    public static async Task StampAsync(UserManager<AppUser> users, AppUser user, DateTimeOffset at)
    {
        await ClearAsync(users, user);
        await users.AddClaimAsync(user, Create(at));
    }

    public static async Task ClearAsync(UserManager<AppUser> users, AppUser user)
    {
        var existing = (await users.GetClaimsAsync(user)).Where(c => c.Type == Type).ToList();
        if (existing.Count > 0)
        {
            await users.RemoveClaimsAsync(user, existing);
        }
    }
}

/// <summary>
/// Removes accounts that never set a password within the confirmation window (Q-011), so a squatted address is freed.
/// Runs on a <see cref="PeriodicTimer"/> driven by <see cref="TimeProvider"/>. A pass deletes in batches and logs only
/// counts, never addresses. Unconfirmed accounts have no memberships or invites, so the foreign keys do not block.
/// </summary>
internal sealed class UnconfirmedAccountCleanup(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<AuthOptions> options,
    ILogger<UnconfirmedAccountCleanup> logger) : BackgroundService
{
    private const int MaxBatchesPerPass = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.UnconfirmedCleanupIntervalMinutes));
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next tick tries again; a database outage must not stop the host.
                    logger.LogWarning(ex, "Cleaning up unconfirmed accounts failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass. Returns how many accounts it deleted.</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var batch = Math.Max(1, settings.UnconfirmedCleanupBatchSize);
        var now = time.GetUtcNow();
        var cutoff = RegistrationClaim.Value(now.AddHours(-settings.ConfirmationTokenLifetimeHours));

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        // Unconfirmed accounts from before the claim existed: start their window now rather than keep them forever.
        var unstamped = await db.Users
            .Where(u => !u.EmailConfirmed && !db.UserClaims.Any(c => c.UserId == u.Id && c.ClaimType == RegistrationClaim.Type))
            .OrderBy(u => u.Id)
            .Take(batch)
            .Select(u => u.Id)
            .ToListAsync(ct);
        foreach (var id in unstamped)
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is { EmailConfirmed: false })
            {
                await users.AddClaimAsync(user, RegistrationClaim.Create(now));
            }
        }

        var deleted = 0;
        for (var round = 0; round < MaxBatchesPerPass; round++)
        {
            var expired = await db.UserClaims
                .Where(c => c.ClaimType == RegistrationClaim.Type && c.ClaimValue!.CompareTo(cutoff) < 0)
                .Join(db.Users.Where(u => !u.EmailConfirmed), c => c.UserId, u => u.Id, (c, u) => u.Id)
                .OrderBy(id => id)
                .Take(batch)
                .ToListAsync(ct);
            if (expired.Count == 0)
            {
                break;
            }

            var deletedThisRound = 0;
            foreach (var id in expired)
            {
                // Read again: the person may have confirmed since the list was read. Delete is guarded by the
                // concurrency stamp, so a confirmation that wins the race makes this delete fail instead.
                var user = await users.FindByIdAsync(id.ToString());
                if (user is { EmailConfirmed: false } && (await users.DeleteAsync(user)).Succeeded)
                {
                    deletedThisRound++;
                }
            }

            deleted += deletedThisRound;
            if (expired.Count < batch || deletedThisRound == 0)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("Removed {Count} unconfirmed accounts older than {Hours} hours.", deleted, settings.ConfirmationTokenLifetimeHours);
        }

        return deleted;
    }
}
