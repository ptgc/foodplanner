using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Infrastructure.Persistence;

public enum DatabaseStatus
{
    Up,
    NotConfigured,
    Unreachable,
}

public interface IDatabaseReadiness
{
    Task<DatabaseStatus> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reports whether SQL Server answers. Never throws: a missing connection string or an unreachable
/// server is a status, so the API can still start and answer 503 on the readiness endpoint.
/// </summary>
internal sealed class DatabaseReadiness(IConfiguration configuration, IServiceScopeFactory scopes, ILogger<DatabaseReadiness> logger) : IDatabaseReadiness
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<DatabaseStatus> CheckAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(DependencyInjection.ConnectionStringName)))
        {
            return DatabaseStatus.NotConfigured;
        }

        // A readiness probe must answer fast even when the connection string has no short Connect Timeout.
        // SqlClient can block synchronously (DNS) and ignore cancellation while connecting, so run it on the pool and bound the wait itself.
        try
        {
            await Task.Run(() => ProbeAsync(cancellationToken), CancellationToken.None).WaitAsync(ProbeTimeout, cancellationToken);
            return DatabaseStatus.Up;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Database readiness check failed: {Error}", ex.Message);
            return DatabaseStatus.Unreachable;
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        await db.Database.CloseConnectionAsync();
    }
}
