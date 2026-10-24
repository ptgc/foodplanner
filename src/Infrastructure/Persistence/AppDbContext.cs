using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence.Configurations;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence;

/// <summary>
/// The single application context. It derives from <see cref="IdentityUserContext{TUser, TKey}"/> for local login (T-009).
/// Identity's tables are global (not tenant entities) and its role tables are not used, because roles are per
/// membership (T-011).
/// Tenancy is built in: every <see cref="ITenantEntity"/> gets the global query filter <see cref="TenantFilterName"/>
/// and every save passes the tenancy write guard. Add entities through <c>IEntityTypeConfiguration</c> classes in
/// this assembly; the tenancy conventions run after them. Migrations are never applied at startup.
/// </summary>
public class AppDbContext : IdentityUserContext<AppUser, Guid>
{
    /// <summary>Name of the global tenant query filter (EF Core named filter).</summary>
    public const string TenantFilterName = "Tenant";

    private readonly ICurrentHousehold _currentHousehold;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ICurrentHousehold currentHousehold,
        ITenantWriteScope writeScope,
        TimeProvider timeProvider)
        : this((DbContextOptions)options, currentHousehold, writeScope, timeProvider)
    {
    }

    /// <summary>For test-only subclasses, which need their own options type.</summary>
    protected AppDbContext(
        DbContextOptions options,
        ICurrentHousehold currentHousehold,
        ITenantWriteScope writeScope,
        TimeProvider timeProvider)
        : base(options)
    {
        _currentHousehold = currentHousehold;
        WriteScope = writeScope;
        TimeProvider = timeProvider;
    }

    public DbSet<Household> Households => Set<Household>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Invite> Invites => Set<Invite>();

    /// <summary>Global-or-household catalog (<see cref="ICatalogEntity"/>): reads see global rows and the current household's.</summary>
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();

    /// <summary>Global only. Not household data, so no filter; nothing writes it at request time.</summary>
    public DbSet<Unit> Units => Set<Unit>();

    /// <summary>Read by the tenant query filter on every query. Must stay an instance member (see the filter).</summary>
    internal Guid? CurrentHouseholdId => _currentHousehold.HouseholdId;

    internal ITenantWriteScope WriteScope { get; }

    internal TimeProvider TimeProvider { get; }

    protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Added here, not in DI, so no AppDbContext can be created without the write guard. Sealed so a
        // subclass cannot drop it.
        optionsBuilder.AddInterceptors(TenancySaveChangesInterceptor.Instance);
    }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ConfigureAdditionalModel(modelBuilder);

        // Last, so it covers every entity configured above.
        TenancyModelConventions.Apply(modelBuilder, this);

        // After the conventions: the seed rows carry the audit columns the conventions add.
        CatalogSeedData.Apply(modelBuilder);
    }

    /// <summary>Hook for test-only subclasses to add entities before the tenancy conventions run.</summary>
    protected virtual void ConfigureAdditionalModel(ModelBuilder modelBuilder)
    {
    }
}
