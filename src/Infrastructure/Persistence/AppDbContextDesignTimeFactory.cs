using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Foodplanner.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef migrations</c> and migration bundles only, so they never start the Api host.
/// No connection string here: commands that touch a database take <c>--connection</c>.
/// </summary>
internal sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer().Options;
        return new AppDbContext(options, new NoHousehold(), new TenantWriteScope(), TimeProvider.System);
    }

    private sealed class NoHousehold : ICurrentHousehold
    {
        public Guid? HouseholdId => null;

        public HouseholdRole? Role => null;
    }
}
