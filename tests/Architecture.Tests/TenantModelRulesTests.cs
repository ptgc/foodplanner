using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// Rules over the built EF model of the production <see cref="AppDbContext"/>. They need no database: building the
/// model does not connect. Each rule is also run on a deliberately bad model, so a rule that stops biting fails.
/// </summary>
public class TenantModelRulesTests
{
    private static readonly ConstantHousehold NoHousehold = new();

    [Fact]
    public void Production_model_satisfies_the_tenancy_model_rules()
    {
        var model = BuildModel<AppDbContext>(options => new AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System));

        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Membership)); // not vacuous
        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Invite));
        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(Ingredient)); // a catalog entity (T-020)
        Assert.Empty(TenantModelRules.Violations(model));
    }

    // Gap c. The save interceptor reads CreatedAt and UpdatedAt from every tracked Entity, and the conventions add
    // them only to hierarchy roots that are not owned, so an owned Entity would crash every save of its owner.
    [Fact]
    public void Owned_type_that_derives_from_Entity_is_rejected()
    {
        var model = BuildModel<OwnedEntityContext>(options => new OwnedEntityContext(options));

        var violation = Assert.Single(TenantModelRules.Violations(model));
        Assert.Contains(nameof(OwnedPart), violation, StringComparison.Ordinal);
        Assert.Contains("owned", violation, StringComparison.OrdinalIgnoreCase);
    }

    // Gap d. A HouseholdId column on a type that is not an ITenantEntity gets no query filter and no write guard.
    [Fact]
    public void HouseholdId_on_a_type_that_is_not_a_tenant_entity_is_rejected()
    {
        var model = BuildModel<LeakyContext>(options => new LeakyContext(options));

        var violation = Assert.Single(TenantModelRules.Violations(model));
        Assert.Contains(nameof(LeakyThing), violation, StringComparison.Ordinal);
        Assert.Contains("ITenantEntity", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdId_added_as_a_shadow_property_is_rejected_too()
    {
        var model = BuildModel<ShadowLeakContext>(options => new ShadowLeakContext(options));

        Assert.Contains(TenantModelRules.Violations(model), v => v.Contains(nameof(ShadowLeakThing), StringComparison.Ordinal));
    }

    // N4.
    [Fact]
    public void Implicit_many_to_many_between_tenant_entities_is_rejected()
    {
        var model = BuildModel<ImplicitJoinContext>(options => new ImplicitJoinContext(options));

        var violation = Assert.Single(TenantModelRules.Violations(model));
        Assert.Contains("joins two tenant entities", violation, StringComparison.Ordinal);
    }

    // T-020. A HouseholdId on an ICatalogEntity is allowed, but only with the 'global or mine' filter and the token.
    [Fact]
    public void Catalog_entity_with_the_global_or_mine_filter_is_accepted()
    {
        var model = BuildModel<CatalogContext>(options => new CatalogContext(options));

        Assert.Contains(model.GetEntityTypes(), e => e.ClrType == typeof(CatalogThing)); // not vacuous
        Assert.Empty(TenantModelRules.Violations(model));
    }

    [Fact]
    public void Catalog_entity_with_the_plain_tenant_filter_or_no_filter_is_rejected()
    {
        // The strict tenant filter would hide the global rows from everybody; no filter would show every household's rows.
        var strict = BuildCatalogModel((entityType, builder) =>
            entityType.SetQueryFilter(AppDbContext.TenantFilterName, (System.Linq.Expressions.Expression<Func<CatalogThing, bool>>)(e => e.HouseholdId == Guid.Empty)));
        Assert.Contains(TenantModelRules.Violations(strict), v => v.Contains(nameof(CatalogThing), StringComparison.Ordinal) && v.Contains("global rows", StringComparison.Ordinal));

        var none = BuildCatalogModel((entityType, builder) => entityType.SetQueryFilter(AppDbContext.TenantFilterName, null));
        Assert.Contains(TenantModelRules.Violations(none), v => v.Contains(nameof(CatalogThing), StringComparison.Ordinal) && v.Contains("without the", StringComparison.Ordinal));

        var noToken = BuildCatalogModel((entityType, builder) => entityType.FindProperty(nameof(ICatalogEntity.HouseholdId))!.IsConcurrencyToken = false);
        Assert.Contains(TenantModelRules.Violations(noToken), v => v.Contains(nameof(CatalogThing), StringComparison.Ordinal) && v.Contains("concurrency token", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entity_that_is_both_tenant_and_catalog_is_rejected()
    {
        var model = BuildModel<BothContext>(options => new BothContext(options));

        Assert.Contains(TenantModelRules.Violations(model), v => v.Contains(nameof(BothThing), StringComparison.Ordinal) && v.Contains("Pick one", StringComparison.Ordinal));
    }

    private static IModel BuildCatalogModel(Action<Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType, ModelBuilder> customize)
    {
        var options = new DbContextOptionsBuilder<CatalogContext>().UseSqlServer("Server=unused;Database=unused")
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, Customizer>()
            .EnableServiceProviderCaching(false).Options; // each call must build its own model
        Customizer.Action = customize;
        using var context = new CatalogContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    // Runs after the tenancy conventions, like the T-012 harness' filter remover.
    private sealed class Customizer(Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizerDependencies dependencies)
        : Microsoft.EntityFrameworkCore.Infrastructure.ModelCustomizer(dependencies)
    {
        [ThreadStatic]
        public static Action<Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType, ModelBuilder>? Action;

        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            Action!((Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType)modelBuilder.Model.FindEntityType(typeof(CatalogThing))!, modelBuilder);
        }
    }

    // N1. A second DbContext would bypass the tenant filter, the write guard and the isolation coverage.
    [Fact]
    public void AppDbContext_is_the_only_DbContext_in_the_production_assemblies()
    {
        var assemblies = new[] { typeof(Program).Assembly, typeof(AppDbContext).Assembly, typeof(Household).Assembly };

        Assert.Empty(TenantModelRules.ForbiddenContexts(assemblies.SelectMany(a => a.GetTypes())));
        Assert.Contains(assemblies.SelectMany(a => a.GetTypes()), t => t == typeof(AppDbContext)); // not vacuous

        // The rule fires on a second context and on a subclass of AppDbContext.
        var forbidden = TenantModelRules.ForbiddenContexts([typeof(AppDbContext), typeof(PlainContext), typeof(OwnedEntityContext)]);
        Assert.Contains(typeof(PlainContext), forbidden);
        Assert.Contains(typeof(OwnedEntityContext), forbidden);
        Assert.DoesNotContain(typeof(AppDbContext), forbidden);
    }

    private static IModel BuildModel<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseSqlServer("Server=unused;Database=unused").Options;
        using var context = create(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private sealed class ConstantHousehold : ICurrentHousehold
    {
        public Guid? HouseholdId => null;

        public HouseholdRole? Role => null;
    }

    private sealed class NoopWriteScope : ITenantWriteScope
    {
        public Guid? HouseholdId => null;

        public IDisposable ForHousehold(Guid householdId) => throw new NotSupportedException();
    }

    public sealed class OwnedPart : Entity
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class OwnerThing : Entity
    {
        public OwnedPart Part { get; set; } = new();
    }

    public sealed class LeakyThing : Entity
    {
        public Guid HouseholdId { get; set; }
    }

    public sealed class ShadowLeakThing : Entity
    {
    }

    public sealed class JoinA : Entity, ITenantEntity
    {
        public Guid HouseholdId { get; private set; }

        public List<JoinB> Bs { get; } = [];
    }

    public sealed class JoinB : Entity, ITenantEntity
    {
        public Guid HouseholdId { get; private set; }

        public List<JoinA> As { get; } = [];
    }

    public sealed class CatalogThing : Entity, ICatalogEntity
    {
        public Guid? HouseholdId { get; private set; }
    }

    public sealed class BothThing : Entity, ITenantEntity, ICatalogEntity
    {
        Guid ITenantEntity.HouseholdId => HouseholdId ?? Guid.Empty;

        public Guid? HouseholdId { get; private set; }
    }

    private sealed class CatalogContext(DbContextOptions<CatalogContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CatalogThing>();
    }

    private sealed class BothContext(DbContextOptions<BothContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<BothThing>();
    }

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options);

    private sealed class ImplicitJoinContext(DbContextOptions<ImplicitJoinContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<JoinA>().HasMany(a => a.Bs).WithMany(b => b.As);
    }

    private sealed class OwnedEntityContext(DbContextOptions<OwnedEntityContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<OwnerThing>().OwnsOne(o => o.Part);
    }

    private sealed class LeakyContext(DbContextOptions<LeakyContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<LeakyThing>();
    }

    private sealed class ShadowLeakContext(DbContextOptions<ShadowLeakContext> options)
        : AppDbContext(options, NoHousehold, new NoopWriteScope(), TimeProvider.System)
    {
        protected override void ConfigureAdditionalModel(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ShadowLeakThing>().Property<Guid>("HouseholdId");
    }
}

/// <summary>Model-level tenancy rules (gaps c and d of the T-008 review).</summary>
public static class TenantModelRules
{
    /// <summary>N1: every DbContext type other than AppDbContext itself (subclasses of AppDbContext included).</summary>
    public static List<Type> ForbiddenContexts(IEnumerable<Type> types) =>
        [.. types.Where(t => typeof(DbContext).IsAssignableFrom(t) && t != typeof(AppDbContext) && !t.IsAbstract)];

    public static List<string> Violations(IModel model)
    {
        var violations = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var name = entityType.DisplayName();
            var clr = entityType.ClrType;
            var isTenant = typeof(ITenantEntity).IsAssignableFrom(clr);

            if (entityType.IsOwned() && (typeof(Entity).IsAssignableFrom(clr) || isTenant))
            {
                violations.Add($"{name}: an owned type must be a plain value type, not an Entity or ITenantEntity. The save interceptor reads CreatedAt/UpdatedAt/HouseholdId from every tracked Entity and would fail on it. Make it its own entity, or drop the base class.");
            }

            if (!entityType.IsOwned() && typeof(Entity).IsAssignableFrom(clr)
                && (entityType.FindProperty(CreatedAt) is null || entityType.FindProperty(UpdatedAt) is null))
            {
                violations.Add($"{name}: derives from Entity but has no CreatedAt/UpdatedAt in the model; the save interceptor would crash on it. Its hierarchy root must be an Entity.");
            }

            var isCatalog = typeof(ICatalogEntity).IsAssignableFrom(clr);
            if (isTenant && isCatalog)
            {
                violations.Add($"{name}: implements both ITenantEntity (always owned) and ICatalogEntity (global or owned). Pick one.");
            }

            // T-020: a catalog entity may have a NULL HouseholdId (a global row), so the property is allowed on it, but
            // only as a nullable Guid with the catalog filter and the concurrency token, checked below.
            if (!isTenant && !isCatalog && entityType.FindProperty(nameof(ITenantEntity.HouseholdId)) is not null)
            {
                violations.Add($"{name}: has a HouseholdId property but does not implement ITenantEntity or ICatalogEntity, so it gets no query filter and no write guard. Implement one of them, or rename the property if it is not the owning household.");
            }

            if (isCatalog && !entityType.IsOwned() && !entityType.HasSharedClrType)
            {
                var root = entityType.GetRootType();
                if (root.FindDeclaredQueryFilter(AppDbContext.TenantFilterName) is not { Expression: not null } filter)
                {
                    violations.Add($"{name}: ICatalogEntity without the '{AppDbContext.TenantFilterName}' query filter.");
                }
                else if (filter.Expression?.ToString() is not { } text
                    || !(text.Contains("== null", StringComparison.Ordinal) && text.Contains("CurrentHouseholdId", StringComparison.Ordinal)))
                {
                    violations.Add($"{name}: the '{AppDbContext.TenantFilterName}' filter of a catalog entity must be 'HouseholdId == null || HouseholdId == CurrentHouseholdId' (global rows plus the household's own), but is {filter.Expression}.");
                }

                if (entityType.FindProperty(nameof(ICatalogEntity.HouseholdId)) is not { IsConcurrencyToken: true, IsNullable: true })
                {
                    violations.Add($"{name}: the HouseholdId of a catalog entity must be a nullable column and a concurrency token, so a forged detached entity cannot update another household's row.");
                }
            }

            // N4. A join between two tenant entities must be an explicit entity implementing ITenantEntity. An implicit
            // (shared-type) join entity, or any non-tenant entity pointing at two tenant entities, has no HouseholdId, no
            // filter and no guard, so a row can join two households.
            if (!isTenant && !entityType.IsOwned()
                && entityType.GetForeignKeys().Count(fk => typeof(ITenantEntity).IsAssignableFrom(fk.PrincipalEntityType.ClrType)) >= 2)
            {
                violations.Add($"{name}: joins two tenant entities but is not an ITenantEntity itself. Model the many-to-many with an explicit join entity that implements ITenantEntity (and has its own isolation case).");
            }

            if (isTenant && !entityType.IsOwned() && !entityType.HasSharedClrType)
            {
                var root = entityType.GetRootType();
                if (root.FindDeclaredQueryFilter(AppDbContext.TenantFilterName) is not { Expression: not null })
                {
                    violations.Add($"{name}: ITenantEntity without the '{AppDbContext.TenantFilterName}' query filter.");
                }

                if (entityType.FindProperty(nameof(ITenantEntity.HouseholdId)) is not { IsConcurrencyToken: true })
                {
                    violations.Add($"{name}: HouseholdId is not a concurrency token, so a forged detached entity could update another household's row.");
                }
            }
        }

        return violations;
    }

    private const string CreatedAt = "CreatedAt";
    private const string UpdatedAt = "UpdatedAt";
}
