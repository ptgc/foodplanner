using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Tenancy;
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

    /// <summary>
    /// The same for a photo import (T-118), with its own counters (<c>PhotoDayCalls</c>, <c>PhotoCalls</c>): the household's daily
    /// limit and the global monthly cap. If the global cap refuses, the household's reservation is given back.
    /// </summary>
    Task<AiReservation> TryReservePhotoAsync(CancellationToken cancellationToken);

    /// <summary>Gives a photo reservation back (only when nothing was sent to Claude).</summary>
    Task ReleasePhotoAsync(CancellationToken cancellationToken);

    /// <summary>The current household's photo imports today under its plan: unlimited, or the daily limit and what is left (never negative).</summary>
    Task<PhotoQuota> PhotoQuotaAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What a household may still do with photo imports today (T-123). <c>Unlimited</c> carries no numbers. Lowering a limit below
/// what was used leaves <c>Remaining</c> at 0; a plan change never resets or refunds a counter.
/// </summary>
public sealed record PhotoQuota(bool Unlimited, int? DailyLimit, int? Remaining);

/// <summary>The default plan, read from configuration (T-123). Households without a <c>HouseholdPlan</c> row follow it live.</summary>
public static class HouseholdPlanDefaults
{
    public static HouseholdPlanLimits From(AiImportOptions lineHelp, AiPhotoImportOptions photo) => new(
        AiLimit.Configured(photo.DailyPerHousehold),
        AiLimit.Configured(lineHelp.DailyCallsPerHousehold),
        AiLimit.Configured(lineHelp.MonthlyCallsPerHousehold));
}

/// <summary>
/// Each counter is raised by ONE conditional UPDATE (<c>... WHERE calls &lt; limit</c>), so the check and the increment are one
/// statement the database serialises on the row: two imports at 19 of 20 cannot both get the 20th call. A period that has rolled
/// over (another day or month stored) restarts at one. The first call of a household or month inserts the row; two inserts at
/// once hit the unique index, the loser retries as an update. A call is counted when reserved, whether or not the AI answers
/// (it may still have cost money). The household rows go through the tenant filter, so only the current household is touched.
/// The household's limits come from its <see cref="HouseholdPlan"/> (T-123), read once per reservation through the tenant filter
/// (no row: the default plan from configuration, live). An unlimited limit is passed to the UPDATE as <c>int.MaxValue</c>, so the
/// condition never fails and the counters keep counting (usage metrics); the global caps apply to every household. The plan is
/// read before the single atomic statement, so a plan changed a moment earlier can apply to at most the requests already in flight.
/// </summary>
public sealed class AiUsageLimiter(
    AppDbContext db, TimeProvider time, IOptions<AiImportOptions> options, IOptions<AiPhotoImportOptions>? photoOptions = null) : IAiUsageLimiter
{
    private const int Attempts = 3;

    public async Task<AiReservation> TryReserveAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var day = AiUsagePeriod.Day(now);
        var month = AiUsagePeriod.Month(now);
        var limits = await LimitsAsync(settings, photoOptions?.Value ?? new AiPhotoImportOptions(), cancellationToken);
        if (limits.LineHelpPerDay.IsNone || limits.LineHelpPerMonth.IsNone)
        {
            return AiReservation.HouseholdLimit;
        }

        if (settings.GlobalMonthlyCalls <= 0)
        {
            return AiReservation.GlobalLimit;
        }

        if (!await ReserveHouseholdAsync(now, day, month, limits.LineHelpPerDay.ToCeiling(), limits.LineHelpPerMonth.ToCeiling(), cancellationToken))
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

    /// <summary>The current household's limits: its plan row (tenant filter, so never another household's) or the configured defaults.</summary>
    private async Task<HouseholdPlanLimits> LimitsAsync(AiImportOptions lineHelp, AiPhotoImportOptions photo, CancellationToken cancellationToken)
    {
        var plan = await db.HouseholdPlans.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return HouseholdPlanLimits.Resolve(plan, HouseholdPlanDefaults.From(lineHelp, photo));
    }

    private async Task<bool> ReserveHouseholdAsync(
        DateTimeOffset now, DateOnly day, DateOnly month, int dayLimit, int monthLimit, CancellationToken cancellationToken)
    {
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

            if (await db.AiHouseholdUsages.AnyAsync(
                    u => !((u.MonthStart != month || u.MonthCalls < monthLimit) && (u.Day != day || u.DayCalls < dayLimit)), cancellationToken))
            {
                return false; // The row exists and the condition fails: a limit is reached.
            }

            if (await db.AiHouseholdUsages.AnyAsync(cancellationToken))
            {
                continue; // The row was created by another request after our UPDATE found none: update it on the next pass.
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

            if (await db.AiGlobalUsages.AnyAsync(u => u.MonthStart == month && u.Calls >= cap, cancellationToken))
            {
                return false; // The month's row exists and the cap is reached.
            }

            if (await db.AiGlobalUsages.AnyAsync(u => u.MonthStart == month, cancellationToken))
            {
                continue; // Created by another request after our UPDATE found none: update it on the next pass.
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

    public async Task<AiReservation> TryReservePhotoAsync(CancellationToken cancellationToken)
    {
        var settings = photoOptions?.Value ?? new AiPhotoImportOptions();
        var now = time.GetUtcNow();
        var day = AiUsagePeriod.Day(now);
        var month = AiUsagePeriod.Month(now);
        var limits = await LimitsAsync(options.Value, settings, cancellationToken);
        if (limits.PhotoImportsPerDay.IsNone)
        {
            return AiReservation.HouseholdLimit;
        }

        if (settings.GlobalMonthly <= 0)
        {
            return AiReservation.GlobalLimit;
        }

        if (!await ReservePhotoHouseholdAsync(now, day, limits.PhotoImportsPerDay.ToCeiling(), cancellationToken))
        {
            return AiReservation.HouseholdLimit;
        }

        if (!await ReservePhotoGlobalAsync(now, month, settings.GlobalMonthly, cancellationToken))
        {
            await ReleasePhotoHouseholdAsync(day, cancellationToken);
            return AiReservation.GlobalLimit;
        }

        return AiReservation.Reserved;
    }

    public async Task ReleasePhotoAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await ReleasePhotoHouseholdAsync(AiUsagePeriod.Day(now), cancellationToken);
        var month = AiUsagePeriod.Month(now);
        await db.AiGlobalUsages
            .Where(u => u.MonthStart == month && u.PhotoCalls > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PhotoCalls, u => u.PhotoCalls - 1), cancellationToken);
    }

    public async Task<PhotoQuota> PhotoQuotaAsync(CancellationToken cancellationToken)
    {
        var limit = (await LimitsAsync(options.Value, photoOptions?.Value ?? new AiPhotoImportOptions(), cancellationToken)).PhotoImportsPerDay;
        if (limit.Count is not { } daily)
        {
            return new PhotoQuota(true, null, null);
        }

        var day = AiUsagePeriod.Day(time.GetUtcNow());
        var used = await db.AiHouseholdUsages.AsNoTracking().Where(u => u.PhotoDay == day).Select(u => u.PhotoDayCalls).FirstOrDefaultAsync(cancellationToken);
        return new PhotoQuota(false, daily, Math.Max(0, daily - used));
    }

    private async Task<bool> ReservePhotoHouseholdAsync(DateTimeOffset now, DateOnly day, int limit, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var updated = await db.AiHouseholdUsages
                .Where(u => u.PhotoDay != day || u.PhotoDayCalls < limit)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(u => u.PhotoDayCalls, u => u.PhotoDay == day ? u.PhotoDayCalls + 1 : 1)
                        .SetProperty(u => u.PhotoDay, day),
                    cancellationToken);
            if (updated == 1)
            {
                return true;
            }

            if (await db.AiHouseholdUsages.AnyAsync(u => u.PhotoDay == day && u.PhotoDayCalls >= limit, cancellationToken))
            {
                return false; // The row exists and the daily limit is reached.
            }

            if (await db.AiHouseholdUsages.AnyAsync(cancellationToken))
            {
                continue; // Created by another request after our UPDATE found none: update it on the next pass.
            }

            var row = AiHouseholdUsage.ForFirstPhotoCall(now);
            db.AiHouseholdUsages.Add(row);
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

    private async Task<bool> ReservePhotoGlobalAsync(DateTimeOffset now, DateOnly month, int cap, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var updated = await db.AiGlobalUsages
                .Where(u => u.MonthStart == month && u.PhotoCalls < cap)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PhotoCalls, u => u.PhotoCalls + 1), cancellationToken);
            if (updated == 1)
            {
                return true;
            }

            if (await db.AiGlobalUsages.AnyAsync(u => u.MonthStart == month && u.PhotoCalls >= cap, cancellationToken))
            {
                return false;
            }

            if (await db.AiGlobalUsages.AnyAsync(u => u.MonthStart == month, cancellationToken))
            {
                continue; // Created by another request after our UPDATE found none: update it on the next pass.
            }

            var row = AiGlobalUsage.ForFirstPhotoCall(now);
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

    private Task<int> ReleasePhotoHouseholdAsync(DateOnly day, CancellationToken cancellationToken) =>
        db.AiHouseholdUsages.ExecuteUpdateAsync(
            s => s.SetProperty(u => u.PhotoDayCalls, u => u.PhotoDay == day && u.PhotoDayCalls > 0 ? u.PhotoDayCalls - 1 : u.PhotoDayCalls),
            cancellationToken);

    /// <summary>Gives the household's call back when the global cap refused it, so a refused import costs the household nothing.</summary>
    private Task<int> ReleaseHouseholdAsync(DateOnly day, DateOnly month, CancellationToken cancellationToken) =>
        db.AiHouseholdUsages.ExecuteUpdateAsync(
            s => s
                .SetProperty(u => u.MonthCalls, u => u.MonthStart == month && u.MonthCalls > 0 ? u.MonthCalls - 1 : u.MonthCalls)
                .SetProperty(u => u.DayCalls, u => u.Day == day && u.DayCalls > 0 ? u.DayCalls - 1 : u.DayCalls),
            cancellationToken);
}
