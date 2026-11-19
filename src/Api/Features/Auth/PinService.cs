using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Auth;

public enum PinOutcome
{
    /// <summary>The PIN (or the proof offered instead) was right; the counters are cleared.</summary>
    Ok,

    /// <summary>The account has no PIN.</summary>
    NotSet,

    /// <summary>The account's PIN is locked; nothing was checked.</summary>
    Locked,

    /// <summary>Wrong; the attempt was counted.</summary>
    Incorrect,
}

public readonly record struct PinCheck(PinOutcome Outcome, int AttemptsRemaining = 0, DateTimeOffset? LockedUntil = null);

/// <summary>
/// The account's PIN and its attempt counter (T-026). The counter is server-side and per account, so clearing cookies or using
/// another device does not reset it. An attempt is RESERVED (counted, and the lock set when it is the last one) with the row
/// version BEFORE the PIN is checked, so parallel guesses cannot all be evaluated against a stale counter: at most
/// <c>PinMaxFailedAttempts</c> candidates are ever hashed before the lock holds. A right answer then clears the reservation.
/// The PIN is stored only as an Identity password hash and is never logged.
/// </summary>
public sealed class PinService(AppDbContext db, IPasswordHasher<AppUser> hasher, IOptions<AuthOptions> options, TimeProvider time)
{
    private const int MaxRetries = 50;

    private int MaxAttempts => Math.Max(1, options.Value.PinMaxFailedAttempts);

    private TimeSpan Lockout => TimeSpan.FromMinutes(Math.Max(1, options.Value.PinLockoutMinutes));

    public Task<bool> HasPinAsync(Guid userId, CancellationToken cancellationToken) =>
        db.UserPins.AsNoTracking().AnyAsync(p => p.UserId == userId, cancellationToken);

    /// <summary>True when <paramref name="candidate"/> matches the stored hash (constant-time inside the Identity hasher).</summary>
    public bool Matches(UserPin pin, string candidate) =>
        hasher.VerifyHashedPassword(new AppUser(), pin.Hash, candidate) is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;

    /// <summary>
    /// Counts one attempt, then asks <paramref name="check"/> whether it was right. While the PIN is locked the check is not
    /// called at all (so a right guess is refused too).
    /// </summary>
    public async Task<PinCheck> AttemptAsync(Guid userId, Func<UserPin, Task<bool>> check, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var pin = await db.UserPins.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (pin is null)
            {
                return new PinCheck(PinOutcome.NotSet);
            }

            var now = time.GetUtcNow();
            if (pin.IsLocked(now))
            {
                return new PinCheck(PinOutcome.Locked, LockedUntil: pin.LockedUntil);
            }

            var failure = pin.RegisterFailure(now, MaxAttempts, Lockout);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear(); // somebody else counted first: read the row again
                continue;
            }

            if (!await check(pin))
            {
                return failure.NowLocked
                    ? new PinCheck(PinOutcome.Locked, LockedUntil: failure.LockedUntil)
                    : new PinCheck(PinOutcome.Incorrect, failure.AttemptsRemaining);
            }

            await ClearAsync(userId, cancellationToken);
            return new PinCheck(PinOutcome.Ok);
        }

        // Every retry lost a race: under that much parallel guessing the safe answer is "locked".
        return new PinCheck(PinOutcome.Locked, LockedUntil: time.GetUtcNow() + Lockout);
    }

    /// <summary>Stores a new PIN, or replaces the existing one (the caller has proved the right to). Counters are reset.</summary>
    public async Task SetAsync(Guid userId, string pin, CancellationToken cancellationToken)
    {
        var hash = hasher.HashPassword(new AppUser(), pin);
        var now = time.GetUtcNow();
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var existing = await db.UserPins.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            try
            {
                if (existing is null)
                {
                    db.UserPins.Add(new UserPin(userId, hash, now));
                }
                else
                {
                    existing.Replace(hash, now);
                }

                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException)
            {
                db.ChangeTracker.Clear(); // a parallel set or attempt: read again and write ours last
            }
        }

        throw new InvalidOperationException("The PIN could not be saved.");
    }

    private async Task ClearAsync(Guid userId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var pin = await db.UserPins.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
            if (pin is null)
            {
                return;
            }

            pin.RegisterSuccess(time.GetUtcNow());
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
