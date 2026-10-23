using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence;

/// <summary>
/// The single application context (T-009 changes its base class for Identity; it stays one context).
/// Tenancy is built in: every <see cref="ITenantEntity"/> gets the global query filter <see cref="TenantFilterName"/>
/// and every save passes the tenancy write guard. Add entities through <c>IEntityTypeConfiguration</c> classes in
/// this assembly; the tenancy conventions run after them. Migrations are never applied at startup.
/// </summary>
public class AppDbContext : DbContext
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

    /// <summary>Read by the tenant query filter on every query. Must stay an instance member (see the filter).</summary>
    internal Guid? CurrentHouseholdId => _currentHousehold.HouseholdId;

    internal ITenantWriteScope WriteScope { get; }

    internal TimeProvider TimeProvider { get; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Added here, not in DI, so no AppDbContext can be created without the write guard.
        optionsBuilder.AddInterceptors(TenancySaveChangesInterceptor.Instance);
    }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ConfigureAdditionalModel(modelBuilder);

        // Last, so it covers every entity configured above.
        TenancyModelConventions.Apply(modelBuilder, this);
    }

    /// <summary>Hook for test-only subclasses to add entities before the tenancy conventions run.</summary>
    protected virtual void ConfigureAdditionalModel(ModelBuilder modelBuilder)
    {
    }
}
