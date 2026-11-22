using Foodplanner.Domain.Support;
using Foodplanner.Domain.Users;

namespace Foodplanner.Domain.Tests.Support;

public class SupportSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Read = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Write = TimeSpan.FromMinutes(15);

    private static SupportSession Start(SupportSessionMode mode = SupportSessionMode.Read, string note = "The owner left the household") =>
        SupportSession.Start(Guid.NewGuid(), Guid.NewGuid(), mode, SupportReasonCategory.LostOwner, note, Now, Read, Write);

    [Fact]
    public void A_read_session_lasts_thirty_minutes_and_a_write_session_fifteen()
    {
        Assert.Equal(Now.AddMinutes(30), Start(SupportSessionMode.Read).ExpiresAt);
        Assert.Equal(Now.AddMinutes(15), Start(SupportSessionMode.Write).ExpiresAt);
    }

    [Fact]
    public void A_session_is_active_until_it_expires_or_ends_and_ends_only_once()
    {
        var session = Start();
        Assert.True(session.IsActive(Now));
        Assert.True(session.IsActive(Now.AddMinutes(29)));
        Assert.False(session.IsActive(Now.AddMinutes(30)));

        session.End(SupportEndedReasons.Ended, Now.AddMinutes(5));
        Assert.False(session.IsActive(Now.AddMinutes(6)));
        session.End(SupportEndedReasons.Replaced, Now.AddMinutes(9));
        Assert.Equal(SupportEndedReasons.Ended, session.EndedReason);
        Assert.Equal(Now.AddMinutes(5), session.EndedAt);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("too short", null)]
    [InlineData("   exactly ten   ", "exactly ten")]
    public void The_note_is_trimmed_and_at_least_ten_characters(string? input, string? expected) =>
        Assert.Equal(expected, SupportSession.NormalizeNote(input));

    [Fact]
    public void The_note_is_at_most_five_hundred_characters()
    {
        Assert.NotNull(SupportSession.NormalizeNote(new string('a', 500)));
        Assert.Null(SupportSession.NormalizeNote(new string('a', 501)));
    }

    [Fact]
    public void A_session_needs_an_admin_a_household_and_a_valid_reason()
    {
        Assert.Throws<ArgumentException>(() => SupportSession.Start(Guid.Empty, Guid.NewGuid(), SupportSessionMode.Read, SupportReasonCategory.Other, "long enough note", Now, Read, Write));
        Assert.Throws<ArgumentException>(() => SupportSession.Start(Guid.NewGuid(), Guid.Empty, SupportSessionMode.Read, SupportReasonCategory.Other, "long enough note", Now, Read, Write));
        Assert.Throws<ArgumentException>(() => Start(note: "short"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportSession.Start(Guid.NewGuid(), Guid.NewGuid(), (SupportSessionMode)9, SupportReasonCategory.Other, "long enough note", Now, Read, Write));
    }
}

public class SystemAdminListTests
{
    [Fact]
    public void An_address_on_the_list_is_matched_ignoring_case_and_whitespace_when_confirmed_and_not_locked()
    {
        string[] list = ["Admin@Example.test", " second@example.test "];
        Assert.True(SystemAdminList.IsAdmin(list, "admin@example.test", emailConfirmed: true, lockedOut: false));
        Assert.True(SystemAdminList.IsAdmin(list, "SECOND@example.test", emailConfirmed: true, lockedOut: false));
    }

    [Fact]
    public void Unconfirmed_locked_out_and_unlisted_accounts_are_not_admins()
    {
        string[] list = ["admin@example.test"];
        Assert.False(SystemAdminList.IsAdmin(list, "admin@example.test", emailConfirmed: false, lockedOut: false));
        Assert.False(SystemAdminList.IsAdmin(list, "admin@example.test", emailConfirmed: true, lockedOut: true));
        Assert.False(SystemAdminList.IsAdmin(list, "other@example.test", emailConfirmed: true, lockedOut: false));
        Assert.False(SystemAdminList.IsAdmin(list, null, emailConfirmed: true, lockedOut: false));
        Assert.False(SystemAdminList.IsAdmin([], "admin@example.test", emailConfirmed: true, lockedOut: false));
        Assert.False(SystemAdminList.IsAdmin(["", "  "], "", emailConfirmed: true, lockedOut: false));
    }

    [Fact]
    public void Empty_entries_and_duplicates_do_not_count_and_the_cap_is_five()
    {
        Assert.Equal(["a@x.test"], SystemAdminList.Normalize(["a@x.test", "A@X.TEST", "", null, " "]));
        Assert.False(SystemAdminList.IsTooLong(["1@x", "2@x", "3@x", "4@x", "5@x", "", "5@X"]));
        Assert.True(SystemAdminList.IsTooLong(["1@x", "2@x", "3@x", "4@x", "5@x", "6@x"]));
    }
}

public class UserPinClearLockoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Clearing_resets_the_counter_and_the_lock_and_keeps_the_hash()
    {
        var pin = new UserPin(Guid.NewGuid(), "hash-value", Now);
        pin.RegisterFailure(Now, maxAttempts: 3, TimeSpan.FromMinutes(2));
        pin.RegisterFailure(Now, maxAttempts: 3, TimeSpan.FromMinutes(2));
        pin.RegisterFailure(Now, maxAttempts: 3, TimeSpan.FromMinutes(2));
        Assert.True(pin.IsLocked(Now));

        Assert.True(pin.ClearLockout(Now.AddMinutes(1)));

        Assert.False(pin.IsLocked(Now.AddMinutes(1)));
        Assert.Equal(0, pin.FailedCount);
        Assert.Null(pin.LockedUntil);
        Assert.Equal("hash-value", pin.Hash);
    }

    [Fact]
    public void Clearing_a_clean_pin_changes_nothing()
    {
        var pin = new UserPin(Guid.NewGuid(), "hash-value", Now);
        Assert.False(pin.ClearLockout(Now.AddMinutes(1)));
        Assert.Equal(Now, pin.UpdatedAt);
    }
}
