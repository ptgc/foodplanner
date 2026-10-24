using System.Linq.Expressions;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Api.Tests.TenantIsolation;

/// <summary>
/// The catalog variant of the harness tested against itself (T-020): an auto-assigned catalog entity (no household
/// constructor parameter, get-only-style property) passes every check, and one whose filter is the strict tenant filter
/// ("only my household", which hides the global rows) fails the global-row visibility check. The household-row checks
/// and the write guard are shared with the tenant variant, which has its own self-tests.
/// </summary>
public sealed class CatalogHarnessSelfTests(SqlServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TenantIsolationCase LeafCase = TenantIsolationCase.ForCatalog<CatalogLeaf>(
        (_, _) => Task.FromResult(new CatalogLeaf { Text = "leaf " + Guid.NewGuid().ToString("N") }),
        entry => entry.Property(l => l.Text).CurrentValue = "changed");

    [Fact]
    public async Task A_correct_auto_assigned_catalog_entity_passes_every_check()
    {
        var env = await NewEnvironmentAsync("catalogok", correct: true);

        await LeafCase.ReadsAreFilteredAsync(env);
        await LeafCase.InsertForOtherHouseholdIsRejectedAsync(env);
        await LeafCase.UpdateIsRejectedAsync(env);
        await LeafCase.DeleteIsRejectedAsync(env);
        await LeafCase.HouseholdIdChangeIsRejectedAsync(env);
        await LeafCase.BulkOperationsRespectTheFilterAsync(env);
        await LeafCase.GlobalRowsAreVisibleToEveryoneAsync(env);
        await LeafCase.GlobalRowsAreReadOnlyAsync(env);
        await LeafCase.BulkOperationsReachGlobalRowsAsync(env);
    }

    [Fact]
    public async Task A_catalog_entity_with_the_strict_tenant_filter_fails_the_global_row_check()
    {
        var env = await NewEnvironmentAsync("catalogbroken", correct: false);

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => LeafCase.GlobalRowsAreVisibleToEveryoneAsync(env));
        // The write guard does not depend on the filter, so global rows stay read-only even with it broken... as far as
        // the guard goes: the row is invisible to households here, so the read-only check cannot even load it.
        await LeafCase.InsertForOtherHouseholdIsRejectedAsync(env);
    }

    [Fact]
    public async Task Using_the_catalog_checks_on_a_tenant_registration_fails_loudly()
    {
        var env = await NewEnvironmentAsync("catalogmisuse", correct: true);
        var tenantCase = TenantIsolationCase.For<TestNote>((_, _) => Task.FromResult(new TestNote("n")), e => e.Property(n => n.Text).CurrentValue = "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => tenantCase.GlobalRowsAreVisibleToEveryoneAsync(env));
    }

    private async Task<TenantIsolationEnvironment> NewEnvironmentAsync(string name, bool correct)
    {
        var connectionString = await server.CreateDatabaseAsync(name, Ct);
        await using (var setup = CatalogLeafContext.Create(connectionString, null, correct))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        return new TenantIsolationEnvironment(connectionString, (cs, household) => CatalogLeafContext.Create(cs, household, correct));
    }
}

/// <summary>Test-only catalog entity whose household is assigned by Infrastructure on Add (starts as <see cref="Guid.Empty"/>).</summary>
public sealed class CatalogLeaf : Entity, ICatalogEntity
{
    public Guid? HouseholdId { get; private set; } = Guid.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class CatalogLeafContext : AppDbContext
{
    public Guid? Household { get; }

    private CatalogLeafContext(DbContextOptions<CatalogLeafContext> options, Guid? household)
        : base(options, new FixedHousehold(household), new TenantWriteScope(), TimeProvider.System)
    {
        Household = household;
    }

    public static CatalogLeafContext Create(string connectionString, Guid? household, bool correct)
    {
        var options = new DbContextOptionsBuilder<CatalogLeafContext>().UseSqlServer(connectionString);
        if (!correct)
        {
            options.ReplaceService<IModelCustomizer, StrictFilter>();
        }

        // The model differs between the two variants, so never share a cached service provider.
        return new(options.EnableServiceProviderCaching(false).Options, household);
    }

    protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<CatalogLeaf>().ToTable("CatalogLeaves");

    /// <summary>Replaces the "global or mine" filter with the strict tenant one, after the tenancy conventions have run.</summary>
    private sealed class StrictFilter(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var leaf = (IMutableEntityType)modelBuilder.Model.FindEntityType(typeof(CatalogLeaf))!;
            var current = (CatalogLeafContext)context;
            var entity = Expression.Parameter(typeof(CatalogLeaf), "e");
            var filter = Expression.Lambda(
                Expression.Equal(Expression.Property(entity, nameof(CatalogLeaf.HouseholdId)), Expression.Property(Expression.Constant(current), nameof(Household))),
                entity);
            leaf.SetQueryFilter(AppDbContext.TenantFilterName, filter);
        }
    }
}
