using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// Gap e: a tenant row that points at another household's row. Demonstrates the hole the filter and guard leave open,
/// the lookup pattern that closes it, and the reusable <see cref="CrossHouseholdLinks"/> assertion. See that class for
/// the rule T-021+ must follow. Uses a scratch context with its own tenant entities; never part of the product model.
/// </summary>
public sealed class CrossHouseholdLinksTests(SqlServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NewDatabaseAsync()
    {
        var connectionString = await server.CreateDatabaseAsync("xref", Ct);
        await using var context = CrossReferenceContext.Create(connectionString, null);
        await context.Database.EnsureCreatedAsync(Ct);
        return connectionString;
    }

    private static async Task<Guid> AddProjectAsync(string connectionString, Guid household)
    {
        await using var context = CrossReferenceContext.Create(connectionString, household);
        var project = new XProject { Name = "project of " + household };
        context.Add(project);
        await context.SaveChangesAsync(Ct);
        return project.Id;
    }

    [Fact]
    public async Task The_check_derives_a_query_for_each_relationship_between_tenant_entities()
    {
        await using var context = CrossReferenceContext.Create("Server=unused;Database=unused", null);

        var descriptions = CrossHouseholdLinks.Queries(context.Model).Select(q => q.Description).ToList();

        Assert.Contains(descriptions, d => d.StartsWith("XTask.ProjectId -> XProject", StringComparison.Ordinal));
        Assert.Contains(descriptions, d => d.Contains("joins XLabel and XProject", StringComparison.Ordinal) || d.Contains("joins XProject and XLabel", StringComparison.Ordinal));
        // The production model must also yield queries without throwing (none yet: Household is not a tenant entity).
        _ = CrossHouseholdLinks.Queries(TenantIsolationEnvironment.ProductionModel());
    }

    [Fact]
    public async Task Links_within_one_household_are_clean()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        var project = await AddProjectAsync(cs, a);
        await using (var context = CrossReferenceContext.Create(cs, a))
        {
            var label = new XLabel { Name = "label" };
            label.Projects.Add(await context.Set<XProject>().SingleAsync(p => p.Id == project, Ct));
            context.Add(label);
            context.Add(new XTask { ProjectId = project });
            await context.SaveChangesAsync(Ct);
        }

        await using var model = CrossReferenceContext.Create(cs, null);
        await CrossHouseholdLinks.AssertNoneAsync(model.Model, cs);
    }

    [Fact]
    public async Task A_foreign_key_to_another_households_row_is_refused_on_save_and_a_planted_one_is_found_by_the_check()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var projectOfB = await AddProjectAsync(cs, b);

        // This is what a careless endpoint does with a client-supplied id. Since T-073 the save guard refuses it.
        await using (var context = CrossReferenceContext.Create(cs, a))
        {
            context.Add(new XTask { ProjectId = projectOfB });
            await Assert.ThrowsAsync<TenancyViolationException>(() => context.SaveChangesAsync(Ct));
        }

        // The finder is the backstop's own check (and a test helper), so plant the link with raw SQL, bypassing EF.
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(cs))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO XTasks (Id, HouseholdId, ProjectId, CreatedAt, UpdatedAt) VALUES (NEWID(), @household, @project, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())";
            command.Parameters.AddWithValue("@household", a);
            command.Parameters.AddWithValue("@project", projectOfB);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using var model = CrossReferenceContext.Create(cs, null);
        var violations = await CrossHouseholdLinks.FindAsync(model.Model, cs);
        var violation = Assert.Single(violations);
        Assert.Contains("XTask.ProjectId", violation, StringComparison.Ordinal);
        Assert.Contains("another household", violation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_many_to_many_join_row_to_another_households_row_is_found_by_the_check()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var projectOfB = await AddProjectAsync(cs, b);
        XProject detachedProjectOfB;
        await using (var ofB = CrossReferenceContext.Create(cs, b))
        {
            detachedProjectOfB = await ofB.Set<XProject>().AsNoTracking().SingleAsync(p => p.Id == projectOfB, Ct);
        }

        await using (var context = CrossReferenceContext.Create(cs, a))
        {
            var label = new XLabel { Name = "label of A" };
            context.Attach(detachedProjectOfB);
            label.Projects.Add(detachedProjectOfB);
            context.Add(label);
            await context.SaveChangesAsync(Ct);
        }

        await using var model = CrossReferenceContext.Create(cs, null);
        var violation = Assert.Single(await CrossHouseholdLinks.FindAsync(model.Model, cs));
        Assert.Contains("joins", violation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_the_referenced_id_through_the_filtered_set_closes_the_hole()
    {
        var cs = await NewDatabaseAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var projectOfB = await AddProjectAsync(cs, b);

        // The rule for feature code: look the parent up first. From A, B's project does not exist.
        await using (var context = CrossReferenceContext.Create(cs, a))
        {
            Assert.Null(await context.Set<XProject>().FindAsync([projectOfB], Ct));
            Assert.Null(await context.Set<XProject>().SingleOrDefaultAsync(p => p.Id == projectOfB, Ct));
        }

        await using var model = CrossReferenceContext.Create(cs, null);
        await CrossHouseholdLinks.AssertNoneAsync(model.Model, cs);
    }
}

public sealed class XProject : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public string Name { get; set; } = string.Empty;

    public List<XLabel> Labels { get; } = [];
}

public sealed class XTask : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public Guid ProjectId { get; set; }
}

public sealed class XLabel : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public string Name { get; set; } = string.Empty;

    public List<XProject> Projects { get; } = [];
}

/// <summary>Scratch model: a tenant FK (task to project) and a tenant many-to-many (label and project).</summary>
public sealed class CrossReferenceContext : AppDbContext
{
    private CrossReferenceContext(DbContextOptions<CrossReferenceContext> options, Guid? household)
        : base(options, new FixedHousehold(household), new TenantWriteScope(), TimeProvider.System)
    {
    }

    public static CrossReferenceContext Create(string connectionString, Guid? household) =>
        new(new DbContextOptionsBuilder<CrossReferenceContext>().UseSqlServer(connectionString).Options, household);

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<XProject>().ToTable("XProjects").HasMany(p => p.Labels).WithMany(l => l.Projects);
        modelBuilder.Entity<XLabel>().ToTable("XLabels");
        modelBuilder.Entity<XTask>().ToTable("XTasks").HasOne<XProject>().WithMany().HasForeignKey(t => t.ProjectId);
    }
}
