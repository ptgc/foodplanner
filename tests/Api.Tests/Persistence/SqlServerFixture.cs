using System.Security.Cryptography;
using Foodplanner.Api.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlServerFixture))]

namespace Foodplanner.Api.Tests.Persistence;

/// <summary>
/// One SQL Server container per test assembly. Test classes ask for a fresh database with
/// <see cref="CreateDatabaseAsync"/>. The SA password is generated at runtime and never written to a file.
/// Engine discovery is Testcontainers' default: DOCKER_HOST if set, otherwise the local Docker endpoint
/// (npipe://./pipe/docker_engine on Windows, which Rancher Desktop with dockerd (moby) provides).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;

    private string ServerConnectionString =>
        _container?.GetConnectionString() ?? throw new InvalidOperationException("SQL Server container not started.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder(Image).WithPassword(GenerateSaPassword()).Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Api.Tests need a running container engine for the SQL Server Testcontainer. Start Rancher Desktop "
                + "(dockerd/moby) or Docker and check `docker info`. If the engine is not on the default endpoint, set "
                + "DOCKER_HOST (see README, Testcontainers). Cause: " + ex.Message,
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates an empty, uniquely named database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix, CancellationToken cancellationToken)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using (var connection = new SqlConnection(ServerConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    // Meets SQL Server's complexity policy: upper, lower, digit and symbol, 8+ characters.
    private static string GenerateSaPassword() =>
        "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
