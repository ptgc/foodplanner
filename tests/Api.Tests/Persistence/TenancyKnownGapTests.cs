using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>
/// Pins two known holes in the tenancy mechanism so nobody mistakes them for protected paths. EF cannot close them
/// (ExecuteUpdate and raw SQL never reach the SaveChanges guard), so Architecture.Tests forbids them in production
/// code instead: <c>HouseholdIdSetters</c> (SetProperty of HouseholdId) and <c>RawSqlUse</c> (ExecuteSql, SqlQuery,
/// FromSql, raw ADO.NET). If one of these tests starts failing because EF or the model now blocks the path, delete the
/// test and say so in docs/domain.md.
/// </summary>
public sealed class TenancyKnownGapTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestNote> SeedAsync(Guid household)
    {
        await using var context = db.Context(household);
        var note = new TestNote("note");
        context.Add(note);
        await context.SaveChangesAsync(Ct);
        return note;
    }

    [Fact]
    public async Task ExecuteUpdate_can_move_the_current_households_rows_to_another_household_and_skips_UpdatedAt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var note = await SeedAsync(a);
        var before = await db.RawRowAsync(note.Id);

        await using (var context = db.Context(a))
        {
            var moved = await context.Set<TestNote>()
                .ExecuteUpdateAsync(set => set.SetProperty(n => EF.Property<Guid>(n, "HouseholdId"), b), Ct);
            Assert.Equal(1, moved);
        }

        var after = await db.RawRowAsync(note.Id);
        Assert.Equal(b, after!.Value.HouseholdId); // the row moved, with no guard in the way
        Assert.Equal(before!.Value.UpdatedAt, after.Value.UpdatedAt); // and the audit timestamp did not move
    }

    [Fact]
    public async Task Raw_SQL_through_Database_bypasses_the_filter_and_reaches_another_households_rows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var note = await SeedAsync(b);

        await using (var context = db.Context(a))
        {
            var deleted = await context.Database.ExecuteSqlRawAsync("DELETE FROM TestNotes WHERE Id = {0}", [note.Id], Ct);
            Assert.Equal(1, deleted);
        }

        Assert.Null(await db.RawRowAsync(note.Id));
    }
}
