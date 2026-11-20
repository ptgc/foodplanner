using System.Net;
using Foodplanner.Domain.ShoppingLists.Cart;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Oda;

/// <summary>
/// The transport under <see cref="OdaHttp"/>. The default is a plain socket handler that never follows redirects; tests replace it
/// with a fake Oda (no test ever reaches the real one). The host allow-list sits above it, so a fake cannot bypass it.
/// </summary>
public sealed record OdaInnerHandler(HttpMessageHandler Handler)
{
    public static OdaInnerHandler CreateDefault() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false });
}

/// <summary>One reply from Oda, read completely. The body is never logged or put in an exception message.</summary>
public sealed record OdaResponse(HttpStatusCode Status, string Body, string? MediaType, string? SessionId)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>
/// The only place that talks to Oda over HTTP (the outbound-networking architecture rule allows this file). It calls only
/// allow-listed https hosts (<see cref="OdaOptions.AllowedHosts"/>), follows no redirects and turns every transport failure into
/// <see cref="ProviderUnavailableException"/> with a fixed message. One long-lived client, no per-user state.
/// </summary>
public sealed class OdaHttp : IDisposable
{
    private readonly HttpClient _client;

    public OdaHttp(OdaInnerHandler inner, IOptions<OdaOptions> options)
    {
        var settings = options.Value;
        _client = new HttpClient(new AllowListHandler(settings, inner.Handler), disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
    }

    public async Task<OdaResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
            return new OdaResponse(response.StatusCode, body, response.Content.Headers.ContentType?.MediaType, sessionId);
        }
        catch (HttpRequestException)
        {
            throw new ProviderUnavailableException("Oda could not be reached.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderUnavailableException("Oda did not answer in time.");
        }
    }

    public void Dispose() => _client.Dispose();

    private sealed class AllowListHandler(OdaOptions options, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || !options.IsAllowed(uri))
            {
                throw new ProviderUnavailableException("A host outside the Oda allow-list was refused.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
