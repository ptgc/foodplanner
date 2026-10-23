using Foodplanner.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers persistence. The connection string comes from configuration (user secrets in
    /// Development, ConnectionStrings__Default in containers) and is read lazily; there is no
    /// default password. Migrations are never applied here.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString(ConnectionStringName)));
        services.AddScoped<IDatabaseReadiness, DatabaseReadiness>();
        return services;
    }
}
