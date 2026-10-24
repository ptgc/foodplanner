using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.TenantIsolation;

public sealed class XCatalogItem : Entity, ICatalogEntity
{
    public Guid? HouseholdId { get; private set; } = Guid.Empty; // unset: Infrastructure stamps the household on Add

    public string Name { get; set; } = string.Empty;

    /// <summary>Optional pointer at a household's project, to prove a global row that points at household data is found.</summary>
    public Guid? ProjectId { get; set; }
}

public sealed class XNote : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public Guid ItemId { get; set; }
}

/// <summary>T-020: the link check for a catalog entity as the principal or the dependent. A NULL (global) principal is not a violation.</summary>
public sealed class CrossHouseholdCatalogLinksTests(SqlServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync()
    {
        var connectionString = await server.CreateDatabaseAsync("xcat", Ct);
        await using var context = CatalogReferenceContext.Create(connectionString, null);
        await context.Database.EnsureCreatedAsync(Ct);
        return connectionString;
    }

    private static async Task<Guid> AddItemAsync(string cs, Guid household, bool global, Guid? project = null)
    {
        Guid id;
        await using (var context = CatalogReferenceContext.Create(cs, household))
        {
            var item = new XCatalogItem { Name = "item " + Guid.NewGuid(), ProjectId = project };
            context.Add(item);
            await context.SaveChangesAsync(Ct);
            id = item.Id;
        }

        if (global)
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE XCatalogItems SET HouseholdId = NULL WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return id;
    }

    private static async Task AddNoteAsync(string cs, Guid household, Guid item)
    {
        await using var context = CatalogReferenceContext.Create(cs, household);
        context.Add(new XNote { ItemId = item });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<List<string>> FindAsync(string cs)
    {
        await using var model = CatalogReferenceContext.Create(cs, null);
        return await CrossHouseholdLinks.FindAsync(model.Model, cs);
    }

    [Fact]
    public async Task The_check_derives_queries_for_catalog_principals_and_dependents()
    {
        await using var context = CatalogReferenceContext.Create("Server=unused;Database=unused", null);

        var descriptions = CrossHouseholdLinks.Queries(context.Model).Select(q => q.Description).ToList();

        Assert.Contains(descriptions, d => d.StartsWith("XNote.ItemId -> XCatalogItem", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.StartsWith("XCatalogItem.ProjectId -> XProject", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_household_row_pointing_at_a_global_or_its_own_row_is_clean()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        var global = await AddItemAsync(cs, Guid.NewGuid(), global: true);
        var own = await AddItemAsync(cs, a, global: false);

        await AddNoteAsync(cs, a, global);
        await AddNoteAsync(cs, a, own);

        Assert.Empty(await FindAsync(cs));
    }

    [Fact]
    public async Task A_household_row_pointing_at_another_households_catalog_row_is_found()
    {
        var cs = await NewDatabaseAsync();
        var itemOfB = await AddItemAsync(cs, Guid.NewGuid(), global: false);

        await AddNoteAsync(cs, Guid.NewGuid(), itemOfB); // the hole: EF accepts it

        var violation = Assert.Single(await FindAsync(cs));
        Assert.Contains("XNote.ItemId", violation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_global_row_pointing_at_household_data_is_found_and_one_pointing_at_nothing_is_not()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        Guid project;
        await using (var context = CatalogReferenceContext.Create(cs, a))
        {
            var p = new XProject { Name = "p" };
            context.Add(p);
            await context.SaveChangesAsync(Ct);
            project = p.Id;
        }

        await AddItemAsync(cs, Guid.NewGuid(), global: true);
        Assert.Empty(await FindAsync(cs));

        await AddItemAsync(cs, a, global: true, project: project);
        Assert.Contains("XCatalogItem.ProjectId", Assert.Single(await FindAsync(cs)), StringComparison.Ordinal);
    }
}

public sealed class CatalogReferenceContext : AppDbContext
{
    private CatalogReferenceContext(DbContextOptions<CatalogReferenceContext> options, Guid? household)
        : base(options, new FixedHousehold(household), new TenantWriteScope(), TimeProvider.System)
    {
    }

    public static CatalogReferenceContext Create(string connectionString, Guid? household) =>
        new(new DbContextOptionsBuilder<CatalogReferenceContext>().UseSqlServer(connectionString).Options, household);

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<XProject>().ToTable("XProjects");
        modelBuilder.Entity<XCatalogItem>().ToTable("XCatalogItems").HasOne<XProject>().WithMany().HasForeignKey(i => i.ProjectId);
        modelBuilder.Entity<XNote>().ToTable("XNotes").HasOne<XCatalogItem>().WithMany().HasForeignKey(n => n.ItemId);
    }
}
