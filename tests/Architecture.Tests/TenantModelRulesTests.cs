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

            if (!isTenant && entityType.FindProperty(nameof(ITenantEntity.HouseholdId)) is not null)
            {
                violations.Add($"{name}: has a HouseholdId property but does not implement ITenantEntity, so it gets no query filter and no write guard. Implement ITenantEntity, or rename the property if it is not the owning household.");
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
                if (root.FindDeclaredQueryFilter(AppDbContext.TenantFilterName) is null)
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
