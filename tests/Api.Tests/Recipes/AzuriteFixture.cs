using Foodplanner.Api.Tests.Recipes;
using Testcontainers.Azurite;

[assembly: AssemblyFixture(typeof(AzuriteFixture))]

namespace Foodplanner.Api.Tests.Recipes;

/// <summary>
/// One Azurite (Azure Blob emulator) container per test assembly, same image tag as docker-compose.yml. Tests share it
/// and keep apart by using fresh household and recipe ids in every blob name.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

    private AzuriteContainer? _container;

    /// <summary>The emulator's connection string (its documented development account).</summary>
    public string ConnectionString =>
        _container?.GetConnectionString() ?? throw new InvalidOperationException("Azurite container not started.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            // The Azure SDK is newer than the emulator's newest supported API version.
            _container = new AzuriteBuilder(Image).WithCommand("--skipApiVersionCheck").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Api.Tests need a running container engine for the Azurite Testcontainer. Start Rancher Desktop "
                + "(dockerd/moby) or Docker and check `docker info`. Cause: " + ex.Message,
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
}
