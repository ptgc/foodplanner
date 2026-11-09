using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import.Ai;

public enum AiReservation
{
    Reserved,
    HouseholdLimit,
    GlobalLimit,
}

/// <summary>Counts AI calls against the per-household day and month limits and the global month cap (T-077, Q-028).</summary>
public interface IAiUsageLimiter
{
    /// <summary>
    /// Takes one call from the current household's budget and the global budget, atomically: when this returns
    /// <see cref="AiReservation.Reserved"/> the call is counted, and a limit can never be passed by two requests at once.
    /// </summary>
    Task<AiReservation> TryReserveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Each counter is raised by ONE conditional UPDATE (<c>... WHERE calls &lt; limit</c>), so the check and the increment are one
/// statement the database serialises on the row: two imports at 19 of 20 cannot both get the 20th call. A period that has rolled
/// over (another day or month stored) restarts at one. The first call of a household or month inserts the row; two inserts at
/// once hit the unique index, the loser retries as an update. A call is counted when reserved, whether or not the AI answers
/// (it may still have cost money). The household rows go through the tenant filter, so only the current household is touched.
/// </summary>
public sealed class AiUsageLimiter(AppDbContext db, TimeProvider time, IOptions<AiImportOptions> options) : IAiUsageLimiter
{
    private const int Attempts = 3;

    public async Task<AiReservation> TryReserveAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var day = AiUsagePeriod.Day(now);
        var month = AiUsagePeriod.Month(now);
        if (settings.DailyCallsPerHousehold <= 0 || settings.MonthlyCallsPerHousehold <= 0)
        {
            return AiReservation.HouseholdLimit;
        }

        if (settings.GlobalMonthlyCalls <= 0)
        {
            return AiReservation.GlobalLimit;
        }

        if (!await ReserveHouseholdAsync(now, day, month, settings, cancellationToken))
        {
            return AiReservation.HouseholdLimit;
        }

        if (!await ReserveGlobalAsync(now, month, settings.GlobalMonthlyCalls, cancellationToken))
        {
            await ReleaseHouseholdAsync(day, month, cancellationToken);
            return AiReservation.GlobalLimit;
        }

        return AiReservation.Reserved;
    }

    private async Task<bool> ReserveHouseholdAsync(
        DateTimeOffset now, DateOnly day, DateOnly month, AiImportOptions settings, CancellationToken cancellationToken)
    {
        var dayLimit = settings.DailyCallsPerHousehold;
        var monthLimit = settings.MonthlyCallsPerHousehold;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // UpdatedAt is intentionally not set by these ExecuteUpdate calls: a counter row's UpdatedAt is not read anywhere, and
            // no architecture rule requires a bulk write to touch it (the bulk path skips the save interceptor by design).
            var updated = await db.AiHouseholdUsages
                .Where(u => (u.MonthStart != month || u.MonthCalls < monthLimit) && (u.Day != day || u.DayCalls < dayLimit))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(u => u.MonthCalls, u => u.MonthStart == month ? u.MonthCalls + 1 : 1)
                        .SetProperty(u => u.DayCalls, u => u.Day == day ? u.DayCalls + 1 : 1)
                        .SetProperty(u => u.MonthStart, month)
                        .SetProperty(u => u.Day, day),
                    cancellationToken);
            if (updated == 1)
            {
                return true;
            }

            if (await db.AiHouseholdUsages.AnyAsync(cancellationToken))
            {
                return false; // The row exists and the condition failed: a limit is reached.
            }

            var row = new AiHouseholdUsage(now);
            db.AiHouseholdUsages.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached; // Another request inserted the row first: update it on the next pass.
            }
            catch
            {
                db.Entry(row).State = EntityState.Detached; // Any other failure (timeout, cancel): never leave the Added row for a later SaveChanges.
                throw;
            }
        }

        throw new InvalidOperationException("The AI usage counter could not be updated.");
    }

    private async Task<bool> ReserveGlobalAsync(DateTimeOffset now, DateOnly month, int cap, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var updated = await db.AiGlobalUsages
                .Where(u => u.MonthStart == month && u.Calls < cap)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Calls, u => u.Calls + 1), cancellationToken);
            if (updated == 1)
            {
                return true;
            }

            if (await db.AiGlobalUsages.AnyAsync(u => u.MonthStart == month, cancellationToken))
            {
                return false;
            }

            var row = new AiGlobalUsage(now);
            db.AiGlobalUsages.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
            }
            catch
            {
                db.Entry(row).State = EntityState.Detached;
                throw;
            }
        }

        throw new InvalidOperationException("The AI usage counter could not be updated.");
    }

    /// <summary>Gives the household's call back when the global cap refused it, so a refused import costs the household nothing.</summary>
    private Task<int> ReleaseHouseholdAsync(DateOnly day, DateOnly month, CancellationToken cancellationToken) =>
        db.AiHouseholdUsages.ExecuteUpdateAsync(
            s => s
                .SetProperty(u => u.MonthCalls, u => u.MonthStart == month && u.MonthCalls > 0 ? u.MonthCalls - 1 : u.MonthCalls)
                .SetProperty(u => u.DayCalls, u => u.Day == day && u.DayCalls > 0 ? u.DayCalls - 1 : u.DayCalls),
            cancellationToken);
}
