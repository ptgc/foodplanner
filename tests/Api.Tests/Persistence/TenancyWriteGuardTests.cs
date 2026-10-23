using Foodplanner.Domain.Common;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>Write side of tenancy: the SaveChanges guard, the write scope and audit timestamps.</summary>
public sealed class TenancyWriteGuardTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    /// <summary>Inserts a note for <paramref name="household"/> and returns it detached, carrying its Id and HouseholdId.</summary>
    private async Task<TestNote> SeedAsync(Guid household, string text)
    {
        await using var context = db.Context(household);
        var note = new TestNote(text);
        context.Add(note);
        await context.SaveChangesAsync(Ct);
        return note;
    }

    [Fact]
    public async Task Add_without_household_context_throws()
    {
        await using var none = db.Context(household: null);
        none.Add(new TestNote("no household"));
        none.Add(new TestNote("explicit household", _a));

        await Assert.ThrowsAsync<TenancyViolationException>(() => none.SaveChangesAsync(Ct));

        Assert.Equal(0, await db.RawCountAsync(Guid.Empty));
        Assert.Equal(0, await db.RawCountAsync(_a));
    }

    [Fact]
    public async Task Add_without_household_context_throws_on_synchronous_SaveChanges()
    {
        await using var none = db.Context(household: null);
        none.Add(new TestNote("sync", _a));

        Assert.Throws<TenancyViolationException>(() => none.SaveChanges());

        Assert.Equal(0, await db.RawCountAsync(_a));
    }

    [Fact]
    public async Task Add_sets_household_from_context()
    {
        var note = await SeedAsync(_a, "mine");

        Assert.Equal(_a, note.HouseholdId);
        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Equal(_a, (await db.RawRowAsync(note.Id))?.HouseholdId);
    }

    [Fact]
    public async Task Add_for_other_household_throws()
    {
        await using var a = db.Context(_a);
        a.Add(new TestNote("for b", _b));

        await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal(0, await db.RawCountAsync(_a));
        Assert.Equal(0, await db.RawCountAsync(_b));
    }

    [Fact]
    public async Task Rejected_save_sends_no_sql_for_valid_entries_either()
    {
        await using var a = db.Context(_a);
        a.Add(new TestNote("valid"));
        a.Add(new TestNote("for b", _b));

        await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal(0, await db.RawCountAsync(_a));
    }

    [Fact]
    public async Task Update_of_other_household_row_throws()
    {
        var bNote = await SeedAsync(_b, "b original");
        await using var a = db.Context(_a);
        a.Attach(bNote);
        bNote.Rename("changed by a");

        await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal((_b, "b original"), Pick(await db.RawRowAsync(bNote.Id)));
    }

    [Fact]
    public async Task Delete_of_other_household_row_throws()
    {
        var bNote = await SeedAsync(_b, "b keep");
        await using var a = db.Context(_a);
        a.Attach(bNote);
        a.Remove(bNote);

        await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal((_b, "b keep"), Pick(await db.RawRowAsync(bNote.Id)));
    }

    [Fact]
    public async Task Changing_HouseholdId_throws()
    {
        var aNote = await SeedAsync(_a, "a move");
        await using var a = db.Context(_a);
        var tracked = await a.Set<TestNote>().SingleAsync(n => n.Id == aNote.Id, Ct);
        a.Entry(tracked).Property(n => n.HouseholdId).CurrentValue = _b;

        await Assert.ThrowsAsync<TenancyViolationException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal((_a, "a move"), Pick(await db.RawRowAsync(aNote.Id)));
    }

    [Fact]
    public async Task Update_of_own_detached_row_succeeds()
    {
        var aNote = await SeedAsync(_a, "a before");
        await using var a = db.Context(_a);
        aNote.Rename("a after");
        a.Update(aNote);

        await a.SaveChangesAsync(Ct);

        Assert.Equal((_a, "a after"), Pick(await db.RawRowAsync(aNote.Id)));
    }

    [Fact]
    public async Task Detached_entity_with_own_household_but_other_households_row_id_affects_no_row()
    {
        // HouseholdId is a concurrency token, so the UPDATE also matches the stored HouseholdId in SQL.
        var bNote = await SeedAsync(_b, "b target");
        var forged = new TestNote("forged by a", _a);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(forged, bNote.Id);
        await using var a = db.Context(_a);
        a.Update(forged);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => a.SaveChangesAsync(Ct));

        Assert.Equal((_b, "b target"), Pick(await db.RawRowAsync(bNote.Id)));
    }

    [Fact]
    public async Task Write_scope_allows_explicit_household()
    {
        var h = Guid.NewGuid();
        var scope = new TenantWriteScope();
        await using var none = db.Context(household: null, writeScope: scope);

        using (scope.ForHousehold(h))
        {
            none.Add(new TestNote("in scope", h));
            await none.SaveChangesAsync(Ct);
            Assert.Equal(1, await db.RawCountAsync(h));

            none.Add(new TestNote("other household", _b));
            await Assert.ThrowsAsync<TenancyViolationException>(() => none.SaveChangesAsync(Ct));
            none.ChangeTracker.Clear();
        }

        none.Add(new TestNote("after scope", h));
        await Assert.ThrowsAsync<TenancyViolationException>(() => none.SaveChangesAsync(Ct));

        Assert.Equal(1, await db.RawCountAsync(h));
        Assert.Equal(0, await db.RawCountAsync(_b));
        Assert.Null(scope.HouseholdId);
    }

    [Fact]
    public async Task Write_scope_does_not_allow_delete()
    {
        var h = Guid.NewGuid();
        var note = await SeedAsync(h, "scoped delete");
        var scope = new TenantWriteScope();
        await using var none = db.Context(household: null, writeScope: scope);

        using (scope.ForHousehold(h))
        {
            none.Attach(note);
            none.Remove(note);
            await Assert.ThrowsAsync<TenancyViolationException>(() => none.SaveChangesAsync(Ct));
        }

        Assert.Equal(1, await db.RawCountAsync(h));
    }

    [Fact]
    public void Write_scopes_do_not_nest()
    {
        var scope = new TenantWriteScope();
        using var outer = scope.ForHousehold(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => scope.ForHousehold(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new TenantWriteScope().ForHousehold(Guid.Empty));
    }

    [Fact]
    public async Task CreatedAt_UpdatedAt_set_from_TimeProvider()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var clock = new ManualTimeProvider(created);
        var note = new TestNote("timed");
        await using (var a = db.Context(_a, timeProvider: clock))
        {
            a.Add(note);
            await a.SaveChangesAsync(Ct);
        }

        var inserted = await db.RawRowAsync(note.Id);
        Assert.Equal(created, inserted?.CreatedAt);
        Assert.Equal(created, inserted?.UpdatedAt);

        var later = created.AddHours(5);
        clock.Now = later;
        await using (var a = db.Context(_a, timeProvider: clock))
        {
            var tracked = await a.Set<TestNote>().SingleAsync(n => n.Id == note.Id, Ct);
            tracked.Rename("timed again");
            await a.SaveChangesAsync(Ct);
        }

        var updated = await db.RawRowAsync(note.Id);
        Assert.Equal(created, updated?.CreatedAt);
        Assert.Equal(later, updated?.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, updated?.UpdatedAt.Offset);
    }

    [Fact]
    public async Task Detached_update_keeps_CreatedAt()
    {
        var created = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(created);
        var note = new TestNote("detached timed");
        await using (var a = db.Context(_a, timeProvider: clock))
        {
            a.Add(note);
            await a.SaveChangesAsync(Ct);
        }

        clock.Now = created.AddDays(1);
        await using (var a = db.Context(_a, timeProvider: clock))
        {
            note.Rename("detached timed 2");
            a.Update(note);
            await a.SaveChangesAsync(Ct);
        }

        var row = await db.RawRowAsync(note.Id);
        Assert.Equal(created, row?.CreatedAt);
        Assert.Equal(created.AddDays(1), row?.UpdatedAt);
    }

    private static (Guid, string)? Pick((Guid HouseholdId, string Text, DateTimeOffset, DateTimeOffset)? row) =>
        row is { } r ? (r.HouseholdId, r.Text) : null;
}
