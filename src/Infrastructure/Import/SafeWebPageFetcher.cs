using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Foodplanner.Infrastructure.Import;

/// <summary>
/// SSRF-safe page fetch for recipe import (CLAUDE.md rule 7, T-042). The defences, in the order a request meets them:
/// <list type="number">
/// <item>The URL must be http or https, without credentials, on an allowed port (80 and 443), at most 2000 characters.</item>
/// <item>The connection is made by our own <see cref="SocketsHttpHandler.ConnectCallback"/>: it resolves the host through
/// <see cref="IHostResolver"/>, refuses the whole answer if ANY address is not allowed (<see cref="IFetchPolicy"/>,
/// <see cref="IpAddressRules"/>) and connects to one of the vetted addresses. The address that was checked is the address
/// that is connected to, so a DNS answer that changes between "check" and "connect" (DNS rebinding) cannot help. The callback runs for
/// every new connection, including each redirect hop and IP-literal URLs.</item>
/// <item>Redirects are never followed by the handler. Each hop is followed here, at most <c>MaxRedirects</c> times, and its
/// target URL goes through step 1 and step 2 again.</item>
/// <item>No proxy, no cookies, no credentials, nothing of the caller is forwarded; one fixed User-Agent.</item>
/// <item>Only <c>text/html</c> and <c>application/ld+json</c> are read. The body is streamed (after decompression) into
/// memory and stops at <c>MaxBytes</c>; one total timeout covers connecting, redirects and reading.</item>
/// </list>
/// </summary>
public sealed class SafeWebPageFetcher : IWebPageFetcher, IDisposable
{
    public const string UserAgent = "FoodplannerRecipeImport/1.0";
    public const int MaxUrlLength = 2000;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase) { "text/html", "application/ld+json" };

    private readonly IHostResolver _resolver;
    private readonly IFetchPolicy _policy;
    private readonly RecipeImportOptions _options;
    private readonly HttpClient _client;

    public SafeWebPageFetcher(IHostResolver resolver, IFetchPolicy policy, IOptions<RecipeImportOptions> options)
    {
        _resolver = resolver;
        _policy = policy;
        _options = options.Value;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryValidate(url, out var uri))
        {
            return FetchResult.Fail(FetchFailure.InvalidUrl);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd("text/html, application/ld+json;q=0.9");
                request.Headers.AcceptLanguage.ParseAdd("nb, en;q=0.8");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _options.MaxRedirects)
                    {
                        return FetchResult.Fail(FetchFailure.TooManyRedirects);
                    }

                    var location = response.Headers.Location;
                    if (location is null || !TryValidate(new Uri(uri, location).AbsoluteUri, out var next))
                    {
                        return FetchResult.Fail(FetchFailure.InvalidUrl);
                    }

                    uri = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return FetchResult.Fail(FetchFailure.HttpStatus, (int)response.StatusCode);
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !AllowedContentTypes.Contains(mediaType))
                {
                    return FetchResult.Fail(FetchFailure.UnsupportedContentType);
                }

                if (response.Content.Headers.ContentLength is { } length && length > _options.MaxBytes)
                {
                    return FetchResult.Fail(FetchFailure.TooLarge);
                }

                var bytes = await ReadLimitedAsync(response.Content, timeout.Token);
                return bytes is null
                    ? FetchResult.Fail(FetchFailure.TooLarge)
                    : FetchResult.Ok(uri, Decode(bytes, response.Content.Headers.ContentType), mediaType.ToLowerInvariant());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchResult.Fail(FetchFailure.Timeout);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or InvalidOperationException or UriFormatException)
        {
            return FetchResult.Fail(IsBlocked(exception) ? FetchFailure.BlockedAddress : FetchFailure.NetworkError);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>The URL rules of step 1.</summary>
    private bool TryValidate(string? text, out Uri uri)
    {
        uri = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxUrlLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.UserInfo.Length > 0
            || parsed.Host.Length == 0
            || !_policy.IsPortAllowed(parsed.Port))
        {
            return false;
        }

        // An IP literal can be judged right away; a name is judged when it is resolved at connect time.
        if (IPAddress.TryParse(parsed.DnsSafeHost, out var literal) && !_policy.IsAddressAllowed(literal))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsBlocked(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BlockedAddressException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The whole body, or null when it is longer than the limit (the read stops there).</summary>
    private async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > _options.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset name: UTF-8 is the best guess.
            }
        }

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Step 2: resolve, vet every answer, connect to a vetted address.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        if (!_policy.IsPortAllowed(port))
        {
            throw new BlockedAddressException(host);
        }

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolver.ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // One bad address refuses the whole answer: a name that also points at a private address is a rebinding setup.
        if (addresses.Any(a => !_policy.IsAddressAllowed(a)))
        {
            throw new BlockedAddressException(host);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }
}
