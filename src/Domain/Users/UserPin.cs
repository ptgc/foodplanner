namespace Foodplanner.Domain.Users;

/// <summary>
/// An account's PIN (T-026): one per account, a global per-account secret like the identity tables (no HouseholdId). Only the
/// hash is stored (<see cref="Hash"/>, made by the ASP.NET Identity password hasher, never the PIN itself), with the attempt
/// counter and the lock. Changes go through a row version, so parallel wrong guesses cannot all see a stale counter.
/// </summary>
public sealed class UserPin
{
    private UserPin()
    {
    }

    public UserPin(Guid userId, string hash, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A PIN belongs to an account.", nameof(userId));
        }

        UserId = userId;
        Hash = !string.IsNullOrEmpty(hash) ? hash : throw new ArgumentException("A PIN hash is required.", nameof(hash));
        CreatedAt = UpdatedAt = now;
    }

    public Guid UserId { get; private set; }

    public string Hash { get; private set; } = string.Empty;

    public int FailedCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Replaces the PIN and resets the counters.</summary>
    public void Replace(string hash, DateTimeOffset now)
    {
        Hash = !string.IsNullOrEmpty(hash) ? hash : throw new ArgumentException("A PIN hash is required.", nameof(hash));
        FailedCount = 0;
        LockedUntil = null;
        UpdatedAt = now;
    }

    public bool IsLocked(DateTimeOffset now) => PinLockout.IsLocked(LockedUntil, now);

    /// <summary>Records one wrong PIN. Returns what happened, for the response.</summary>
    public PinFailure RegisterFailure(DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        var result = PinLockout.RecordFailure(FailedCount, now, maxAttempts, lockout);
        FailedCount = result.FailedCount;
        LockedUntil = result.LockedUntil;
        UpdatedAt = now;
        return result;
    }

    /// <summary>A right PIN: counter and lock cleared.</summary>
    public void RegisterSuccess(DateTimeOffset now)
    {
        FailedCount = 0;
        LockedUntil = null;
        UpdatedAt = now;
    }
}
