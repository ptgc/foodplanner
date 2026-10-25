using System.Globalization;
using System.Security.Claims;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
// IdentityUserClaim lives in Microsoft.AspNetCore.Identity.
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

    /// <summary>
    /// Records <paramref name="at"/> as the registration time: removes every earlier claim of this type (duplicates
    /// included) and adds one, in a single save.
    /// </summary>
    public static Task StampAsync(AppDbContext db, Guid userId, DateTimeOffset at) =>
        ReplaceAsync(db, userId, new IdentityUserClaim<Guid> { UserId = userId, ClaimType = Type, ClaimValue = Value(at) });

    /// <summary>Removes the claim once the account is confirmed. A claim left behind is ignored anyway: the cleanup only looks at unconfirmed users.</summary>
    public static Task ClearAsync(AppDbContext db, Guid userId) => ReplaceAsync(db, userId, null);

    // Claim rows have no concurrency token, so a concurrent request that deleted the same rows makes the delete
    // match 0 rows (DbUpdateConcurrencyException). That means the other request did the work: drop the stale
    // entries, look again and apply once more; after a few tries give up quietly, since the claim is only a hint for
    // the cleanup job and must never turn a uniform 202 (or a finished confirmation) into a 500.
    private static async Task ReplaceAsync(AppDbContext db, Guid userId, IdentityUserClaim<Guid>? replacement)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.UserClaims.RemoveRange(await db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == Type).ToListAsync());
            if (replacement is not null)
            {
                db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = userId, ClaimType = Type, ClaimValue = replacement.ClaimValue });
            }

            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Only claim rows are dropped from the tracker (including the pending add); the caller's user stays tracked.
                foreach (var entry in db.ChangeTracker.Entries<IdentityUserClaim<Guid>>().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>The newest recorded time as text, or null if none (an account from before T-072).</summary>
    public static Task<string?> LatestAsync(AppDbContext db, Guid userId) =>
        db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == Type).MaxAsync(c => c.ClaimValue);
}

/// <summary>
/// Removes accounts that never set a password within the confirmation window (Q-011), so a squatted address is freed.
/// Runs on a <see cref="PeriodicTimer"/> driven by <see cref="TimeProvider"/>. A pass deletes in batches and logs only
/// counts, never addresses. Unconfirmed accounts have no memberships or invites, so the foreign keys do not block.
/// </summary>
public sealed class UnconfirmedAccountCleanup(
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
    public async Task<int> SweepAsync(CancellationToken ct)
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
            // The newest claim per user decides, so a duplicate old claim cannot get a refreshed account deleted.
            var expired = await db.Users
                .Where(u => !u.EmailConfirmed
                    && db.UserClaims.Where(c => c.UserId == u.Id && c.ClaimType == RegistrationClaim.Type).Max(c => c.ClaimValue)!.CompareTo(cutoff) < 0)
                .OrderBy(u => u.Id)
                .Select(u => u.Id)
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
