using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>The tenancy conventions on model shapes later features use, such as many-to-many with an implicit join entity.</summary>
public sealed class TenancyModelTests(TenancyDatabase db) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Model_with_many_to_many_join_entity_builds_and_keeps_tenant_filter()
    {
        await using var context = db.Context(Guid.NewGuid());

        var join = Assert.Single(context.Model.GetEntityTypes(), e => e.HasSharedClrType);
        Assert.Null(join.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.Null(join.FindProperty("CreatedAt"));
        Assert.NotNull(context.Model.FindEntityType(typeof(TestNote))!.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.NotNull(context.Model.FindEntityType(typeof(TestBook))!.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.Null(context.Model.FindEntityType(typeof(TestShelf))!.FindDeclaredQueryFilter(AppDbContext.TenantFilterName));
        Assert.NotNull(context.Model.FindEntityType(typeof(TestShelf))!.FindProperty("CreatedAt"));
    }

    [Fact]
    public async Task Many_to_many_rows_are_filtered_by_household()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var contextA = db.Context(a))
        {
            var book = new TestBook { Title = "a book" };
            book.Shelves.Add(new TestShelf { Name = "a shelf" });
            contextA.Add(book);
            await contextA.SaveChangesAsync(Ct);
        }

        await using (var contextA = db.Context(a))
        {
            var books = await contextA.Set<TestBook>().Include(x => x.Shelves).ToListAsync(Ct);
            var book = Assert.Single(books);
            Assert.Equal(a, book.HouseholdId);
            Assert.Equal("a shelf", Assert.Single(book.Shelves).Name);
        }

        await using var contextB = db.Context(b);
        Assert.Empty(await contextB.Set<TestBook>().ToListAsync(Ct));
        Assert.Empty(await contextB.Set<TestShelf>().Where(s => s.Name == "a shelf").SelectMany(s => s.Books).ToListAsync(Ct));
    }
}
