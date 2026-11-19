namespace Foodplanner.Domain.Users;

/// <summary>Result of recording one wrong PIN: the new counter, the lock (if this attempt caused one) and what is left.</summary>
public readonly record struct PinFailure(int FailedCount, DateTimeOffset? LockedUntil, int AttemptsRemaining)
{
    public bool NowLocked => LockedUntil is not null;
}

/// <summary>
/// The PIN attempt counter (T-026): <c>maxAttempts</c> consecutive wrong PINs lock the account's PIN for
/// <c>lockout</c>; the counter then restarts from zero. Pure, so the rule is testable without a database or a clock.
/// </summary>
public static class PinLockout
{
    public static bool IsLocked(DateTimeOffset? lockedUntil, DateTimeOffset now) => lockedUntil is { } until && until > now;

    public static PinFailure RecordFailure(int failedCount, DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        var count = Math.Max(0, failedCount) + 1;
        return count >= maxAttempts
            ? new PinFailure(0, now + lockout, 0)
            : new PinFailure(count, null, maxAttempts - count);
    }
}
