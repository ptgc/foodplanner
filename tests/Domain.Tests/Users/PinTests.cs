using Foodplanner.Domain.Users;

namespace Foodplanner.Domain.Tests.Users;

public class PinTests
{
    [Theory]
    [InlineData("4827", PinProblem.None)]
    [InlineData("0001", PinProblem.None)]
    [InlineData("1357", PinProblem.None)] // steps of 2 are fine
    [InlineData("1235", PinProblem.None)]
    [InlineData("9871", PinProblem.None)]
    [InlineData("0000", PinProblem.TooWeak)]
    [InlineData("7777", PinProblem.TooWeak)]
    [InlineData("1234", PinProblem.TooWeak)]
    [InlineData("0123", PinProblem.TooWeak)]
    [InlineData("6789", PinProblem.TooWeak)]
    [InlineData("9876", PinProblem.TooWeak)]
    [InlineData("3210", PinProblem.TooWeak)]
    [InlineData("123", PinProblem.NotFourDigits)]
    [InlineData("12345", PinProblem.NotFourDigits)]
    [InlineData("12a4", PinProblem.NotFourDigits)]
    [InlineData("١٢٣٤", PinProblem.NotFourDigits)] // non-ASCII digits are not digits here
    [InlineData(" 482", PinProblem.NotFourDigits)]
    [InlineData("", PinProblem.NotFourDigits)]
    [InlineData(null, PinProblem.NotFourDigits)]
    public void The_format_rules(string? pin, PinProblem expected) => Assert.Equal(expected, PinRules.Check(pin));

    [Fact]
    public void Four_wrong_attempts_count_and_the_fifth_locks_for_the_lockout()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var lockout = TimeSpan.FromMinutes(2);
        var count = 0;
        foreach (var remaining in new[] { 4, 3, 2, 1 })
        {
            var failure = PinLockout.RecordFailure(count, now, 5, lockout);
            Assert.Equal(remaining, failure.AttemptsRemaining);
            Assert.False(failure.NowLocked);
            count = failure.FailedCount;
        }

        var fifth = PinLockout.RecordFailure(count, now, 5, lockout);

        Assert.True(fifth.NowLocked);
        Assert.Equal(now + lockout, fifth.LockedUntil);
        Assert.Equal(0, fifth.FailedCount); // the counter restarts after a lock
        Assert.Equal(0, fifth.AttemptsRemaining);
    }

    [Fact]
    public void A_lock_ends_exactly_at_its_time()
    {
        var until = new DateTimeOffset(2026, 10, 31, 12, 2, 0, TimeSpan.Zero);

        Assert.True(PinLockout.IsLocked(until, until.AddSeconds(-1)));
        Assert.False(PinLockout.IsLocked(until, until));
        Assert.False(PinLockout.IsLocked(null, until));
    }

    [Fact]
    public void The_pin_row_counts_locks_and_clears()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var pin = new UserPin(Guid.NewGuid(), "hash-1", now);

        var failure = pin.RegisterFailure(now, 2, TimeSpan.FromMinutes(2));
        Assert.Equal(1, pin.FailedCount);
        Assert.False(pin.IsLocked(now));
        failure = pin.RegisterFailure(now, 2, TimeSpan.FromMinutes(2));
        Assert.True(failure.NowLocked);
        Assert.True(pin.IsLocked(now.AddSeconds(119)));
        Assert.False(pin.IsLocked(now.AddSeconds(120)));

        pin.RegisterSuccess(now);
        Assert.Equal(0, pin.FailedCount);
        Assert.Null(pin.LockedUntil);

        pin.RegisterFailure(now, 5, TimeSpan.FromMinutes(2));
        pin.Replace("hash-2", now);
        Assert.Equal("hash-2", pin.Hash);
        Assert.Equal(0, pin.FailedCount); // replacing resets the counters
        Assert.Throws<ArgumentException>(() => pin.Replace("", now));
        Assert.Throws<ArgumentException>(() => new UserPin(Guid.Empty, "h", now));
    }
}
