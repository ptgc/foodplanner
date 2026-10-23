using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>Read side of tenancy: the global query filter. Each test uses its own household ids.</summary>
public sealed class TenancyFilterTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();

    private async Task<(Guid AId, Guid BId)> SeedAsync()
    {
        Guid aId;
        Guid bId;
        await using (var a = db.Context(_a))
        {
            var note = new TestNote("a1");
            a.Add(note);
            a.Add(new TestNote("a2"));
            await a.SaveChangesAsync(Ct);
            aId = note.Id;
        }

        await using (var b = db.Context(_b))
        {
            var note = new TestNote("b1");
            b.Add(note);
            await b.SaveChangesAsync(Ct);
            bId = note.Id;
        }

        return (aId, bId);
    }

    [Fact]
    public async Task Filter_returns_only_current_household_rows()
    {
        await SeedAsync();

        await using (var a = db.Context(_a))
        {
            var rows = await a.Set<TestNote>().ToListAsync(Ct);
            Assert.Equal(["a1", "a2"], rows.Select(n => n.Text).Order());
            Assert.All(rows, n => Assert.Equal(_a, n.HouseholdId));
        }

        await using (var b = db.Context(_b))
        {
            var rows = await b.Set<TestNote>().ToListAsync(Ct);
            Assert.Equal(["b1"], rows.Select(n => n.Text));
            Assert.All(rows, n => Assert.Equal(_b, n.HouseholdId));
        }
    }

    [Fact]
    public async Task Filter_is_not_cached_across_contexts()
    {
        await SeedAsync();

        // Both contexts are alive at the same time and share the cached model; each must read its own household.
        await using var a = db.Context(_a);
        await using var b = db.Context(_b);

        var fromA = await a.Set<TestNote>().Select(n => n.Text).ToListAsync(Ct);
        var fromB = await b.Set<TestNote>().Select(n => n.Text).ToListAsync(Ct);
        var fromAAgain = await a.Set<TestNote>().Select(n => n.Text).ToListAsync(Ct);

        Assert.Equal(["a1", "a2"], fromA.Order());
        Assert.Equal(["b1"], fromB);
        Assert.Equal(["a1", "a2"], fromAAgain.Order());
        Assert.Same(a.Model, b.Model);
    }

    [Fact]
    public async Task Filter_returns_nothing_without_household_context()
    {
        await SeedAsync();
        await using var none = db.Context(household: null);

        Assert.Equal(0, await none.Set<TestNote>().CountAsync(Ct));
        Assert.Empty(await none.Set<TestNote>().ToListAsync(Ct));
    }

    [Fact]
    public async Task Find_by_id_of_other_household_returns_null()
    {
        var (aId, bId) = await SeedAsync();
        await using var a = db.Context(_a);

        Assert.Null(await a.Set<TestNote>().FindAsync([bId], Ct));
        Assert.Null(await a.Set<TestNote>().FirstOrDefaultAsync(n => n.Id == bId, Ct));
        Assert.NotNull(await a.Set<TestNote>().FindAsync([aId], Ct));
    }

    [Fact]
    public async Task ExecuteDelete_does_not_touch_other_household()
    {
        await SeedAsync();
        await using var a = db.Context(_a);

        var deleted = await a.Set<TestNote>().ExecuteDeleteAsync(Ct);

        Assert.Equal(2, deleted);
        Assert.Equal(0, await db.RawCountAsync(_a));
        Assert.Equal(1, await db.RawCountAsync(_b));
    }

    [Fact]
    public async Task ExecuteUpdate_does_not_touch_other_household()
    {
        await SeedAsync();
        await using var a = db.Context(_a);

        var updated = await a.Set<TestNote>().ExecuteUpdateAsync(s => s.SetProperty(n => n.Text, "changed"), Ct);

        Assert.Equal(2, updated);
        await using var b = db.Context(_b);
        Assert.Equal(["b1"], await b.Set<TestNote>().Select(n => n.Text).ToListAsync(Ct));
    }
}
