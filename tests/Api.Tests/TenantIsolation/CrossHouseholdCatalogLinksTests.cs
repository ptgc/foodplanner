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

        // Since T-073 the save guard refuses this through EF, so plant the link with raw SQL (as a corrupted database would hold it).
        await using (var connection = new SqlConnection(cs))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO XNotes (Id, HouseholdId, ItemId, CreatedAt, UpdatedAt) VALUES (NEWID(), NEWID(), @item, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())";
            command.Parameters.AddWithValue("@item", itemOfB);
            await command.ExecuteNonQueryAsync(Ct);
        }

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

/// <summary>
/// T-073 on the scratch model: a catalog entity that points at household data (XCatalogItem.ProjectId -> XProject) and a
/// tenant entity that points at a catalog entity (XNote.ItemId -> XCatalogItem). The production model has no
/// catalog-to-household key, so this proves the rule for that direction.
/// </summary>
public sealed class SaveTimeReferenceCheckCatalogModelTests(SqlServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync()
    {
        var connectionString = await server.CreateDatabaseAsync("xcatref", Ct);
        await using var context = CatalogReferenceContext.Create(connectionString, null);
        await context.Database.EnsureCreatedAsync(Ct);
        return connectionString;
    }

    private static async Task<Guid> AddProjectAsync(string cs, Guid household)
    {
        await using var context = CatalogReferenceContext.Create(cs, household);
        var project = new XProject { Name = "p" };
        context.Add(project);
        await context.SaveChangesAsync(Ct);
        return project.Id;
    }

    [Fact]
    public async Task A_catalog_foreign_key_is_derived_from_the_model_for_the_catalog_to_household_direction()
    {
        await using var context = CatalogReferenceContext.Create("Server=unused;Database=unused", null);

        var keys = ReferenceForeignKeys.All(context.Model).Select(k => k.ToString()).ToList();

        Assert.Contains("XCatalogItem.ProjectId -> XProject", keys);
        Assert.Contains("XNote.ItemId -> XCatalogItem", keys);
    }

    [Fact]
    public async Task A_household_catalog_row_pointing_at_another_households_row_is_refused_and_at_its_own_saves()
    {
        var cs = await NewDatabaseAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var projectOfA = await AddProjectAsync(cs, a);
        var projectOfB = await AddProjectAsync(cs, b);

        await using (var context = CatalogReferenceContext.Create(cs, a))
        {
            context.Add(new XCatalogItem { Name = "foreign", ProjectId = projectOfB });
            var ex = await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains("XCatalogItem.ProjectId", ex.Message, StringComparison.Ordinal);
            Assert.Contains("XProject", ex.Message, StringComparison.Ordinal);
        }

        await using (var context = CatalogReferenceContext.Create(cs, a))
        {
            var item = new XCatalogItem { Name = "own", ProjectId = projectOfA };
            context.Add(item);
            await context.SaveChangesAsync(Ct);

            item.ProjectId = projectOfB; // modified
            var ex = await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
            Assert.Contains("XCatalogItem.ProjectId", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_household_row_pointing_at_a_global_catalog_row_saves_and_at_another_households_catalog_row_is_refused()
    {
        var cs = await NewDatabaseAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var global = await AddItemAsync(cs, b, global: true);
        var itemOfB = await AddItemAsync(cs, b, global: false);

        await using (var context = CatalogReferenceContext.Create(cs, a))
        {
            context.Add(new XNote { ItemId = global });
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = CatalogReferenceContext.Create(cs, a))
        {
            context.Add(new XNote { ItemId = itemOfB });
            await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
        }
    }

    private static async Task<Guid> AddItemAsync(string cs, Guid household, bool global)
    {
        Guid id;
        await using (var context = CatalogReferenceContext.Create(cs, household))
        {
            var item = new XCatalogItem { Name = "item " + Guid.NewGuid() };
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
