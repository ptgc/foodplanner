using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// The harness tested against itself. An entity whose household is assigned by Infrastructure on Add (the normal
/// pattern: no constructor parameter, no public setter) must pass every check; an entity whose tenant filter has been
/// removed must fail the checks that depend on the filter. Without the second, a harness that asserts nothing would
/// look the same as one that works.
/// </summary>
public sealed class TenantIsolationHarnessSelfTests(TenancyDatabase db, SqlServerFixture server) : IClassFixture<TenancyDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TenantIsolationCase NoteCase = TenantIsolationCase.For<TestNote>(
        (_, _) => Task.FromResult(new TestNote("note")), // ignores the household: Infrastructure assigns it
        entry => entry.Property(n => n.Text).CurrentValue = "changed");

    private static readonly TenantIsolationCase LeafCase = TenantIsolationCase.For<BrokenLeaf>(
        (_, _) => Task.FromResult(new BrokenLeaf { Text = "leaf" }),
        entry => entry.Property(l => l.Text).CurrentValue = "changed");

    private TenantIsolationEnvironment NoteEnvironment() =>
        new(db.ConnectionString, (cs, household) => TestDbContext.Create(cs, household));

    [Fact]
    public async Task An_auto_assigned_entity_passes_every_check()
    {
        var env = NoteEnvironment();

        await NoteCase.ReadsAreFilteredAsync(env);
        await NoteCase.InsertForOtherHouseholdIsRejectedAsync(env);
        await NoteCase.UpdateIsRejectedAsync(env);
        await NoteCase.DeleteIsRejectedAsync(env);
        await NoteCase.HouseholdIdChangeIsRejectedAsync(env);
        await NoteCase.BulkOperationsRespectTheFilterAsync(env);
    }

    [Fact]
    public async Task An_entity_without_its_tenant_filter_fails_the_filter_dependent_checks()
    {
        var connectionString = await server.CreateDatabaseAsync("broken", Ct);
        await using (var setup = BrokenFilterContext.Create(connectionString, null))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        var env = new TenantIsolationEnvironment(connectionString, (cs, household) => BrokenFilterContext.Create(cs, household));

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => LeafCase.ReadsAreFilteredAsync(env));
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => LeafCase.BulkOperationsRespectTheFilterAsync(env));

        // The write guard is independent of the filter, so it still holds: these must pass even for the broken entity.
        await LeafCase.InsertForOtherHouseholdIsRejectedAsync(env);
        await LeafCase.HouseholdIdChangeIsRejectedAsync(env);
    }
}

/// <summary>Test-only tenant entity, in a model whose Tenant filter has been removed for it.</summary>
public sealed class BrokenLeaf : Entity, ITenantEntity
{
    public Guid HouseholdId { get; private set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Removes the Tenant query filter of <see cref="BrokenLeaf"/> after the tenancy conventions have added it.</summary>
public sealed class BrokenFilterContext : AppDbContext
{
    private BrokenFilterContext(DbContextOptions<BrokenFilterContext> options, Guid? household)
        : base(options, new FixedHousehold(household), new TenantWriteScope(), TimeProvider.System)
    {
    }

    public static BrokenFilterContext Create(string connectionString, Guid? household) =>
        new(
            new DbContextOptionsBuilder<BrokenFilterContext>()
                .UseSqlServer(connectionString)
                .ReplaceService<IModelCustomizer, FilterRemover>()
                .Options,
            household);

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BrokenLeaf>().ToTable("BrokenLeaves");

    private sealed class FilterRemover(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var leaf = (IMutableEntityType)modelBuilder.Model.FindEntityType(typeof(BrokenLeaf))!;
            leaf.SetQueryFilter(AppDbContext.TenantFilterName, null);
        }
    }
}
