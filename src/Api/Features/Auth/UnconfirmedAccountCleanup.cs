using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

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
        var cutoff = now.AddHours(-settings.ConfirmationTokenLifetimeHours);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        // Unconfirmed accounts with no registration time (from before T-072): start their window now rather than
        // keep them forever. The update is guarded by the concurrency stamp; losing a race just skips the account.
        var unstamped = await db.Users
            .Where(u => !u.EmailConfirmed && u.RegisteredAt == null)
            .OrderBy(u => u.Id)
            .Take(batch)
            .Select(u => u.Id)
            .ToListAsync(ct);
        foreach (var id in unstamped)
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is { EmailConfirmed: false, RegisteredAt: null })
            {
                user.RegisteredAt = now;
                await users.UpdateAsync(user);
            }
        }

        var deleted = 0;
        for (var round = 0; round < MaxBatchesPerPass; round++)
        {
            // Served by the filtered index on RegisteredAt (unconfirmed users only); no claims table involved.
            var expired = await db.Users
                .Where(u => !u.EmailConfirmed && u.RegisteredAt < cutoff)
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
