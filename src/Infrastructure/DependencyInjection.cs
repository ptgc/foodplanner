using Foodplanner.Infrastructure.Export;
using Foodplanner.Infrastructure.Images;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Foodplanner.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers persistence. The connection string comes from configuration (user secrets in
    /// Development, ConnectionStrings__Default in containers) and is read lazily; there is no
    /// default password. Migrations are never applied here.
    /// The host must register <see cref="Domain.Tenancy.ICurrentHousehold"/> (scoped).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITenantWriteScope, TenantWriteScope>();
        services.AddScoped<ITenantResolution, TenantResolution>();
        services.AddScoped<IInviteRedemption, InviteRedemption>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString(ConnectionStringName)));
        services.AddScoped<IDatabaseReadiness, DatabaseReadiness>();
        services.AddOptions<ImageOptions>().BindConfiguration(ImageOptions.Section);
        services.AddImageStorage();
        services.AddShoppingListExporters();
        return services;
    }
}
