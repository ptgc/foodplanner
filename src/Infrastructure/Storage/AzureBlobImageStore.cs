using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Infrastructure.Storage;

/// <summary>
/// <see cref="IImageStore"/> on Azure Blob Storage (Azurite locally). The connection string is
/// <c>ConnectionStrings:Blobs</c> (<c>UseDevelopmentStorage=true</c> for Azurite; later a managed-identity endpoint).
/// The client is built on first use, so a host without storage configured still starts and only the image routes fail.
/// The container is private; nothing is served from storage directly.
/// </summary>
public sealed class AzureBlobImageStore(IConfiguration configuration) : IImageStore
{
    public const string ConnectionStringName = "Blobs";
    public const string ContainerName = "recipe-images";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private BlobContainerClient? _container;

    public async Task PutAsync(string name, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        await container.GetBlobClient(name).UploadAsync(
            new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        try
        {
            var download = await container.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
        }
    }

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken cancellationToken)
    {
        if (_container is not null)
        {
            return _container;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_container is null)
            {
                var connectionString = configuration.GetConnectionString(ConnectionStringName);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        "ConnectionStrings:Blobs is not set. Locally run ./scripts/local-secrets (UseDevelopmentStorage=true for Azurite); "
                        + "in containers set ConnectionStrings__Blobs.");
                }

                var container = new BlobServiceClient(connectionString).GetBlobContainerClient(ContainerName);
                await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
                _container = container;
            }

            return _container;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class ImageStorageRegistration
{
    public static IServiceCollection AddImageStorage(this IServiceCollection services)
    {
        services.AddSingleton<IImageStore, AzureBlobImageStore>();
        return services;
    }
}
