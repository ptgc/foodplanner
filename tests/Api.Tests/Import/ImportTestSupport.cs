using System.Net;
using System.Net.Sockets;
using System.Text;
using Foodplanner.Infrastructure.Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Import;

/// <summary>One request the test server saw.</summary>
internal sealed record SeenRequest(string Path, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A raw HTTP/1.1 server on a loopback port, so a test controls every byte: redirects, slow bodies, bad content types,
/// oversize and compressed bodies. The handler writes the whole response (status line, headers, body) to the stream. Every
/// response closes the connection, so each request is a new connection (and a new DNS answer for the fetcher).
/// </summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<SeenRequest, Stream, CancellationToken, Task> _handler;
    private readonly Task _loop;
    private readonly List<SeenRequest> _seen = [];

    public TestHttpServer(Func<SeenRequest, Stream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string path = "/") => $"http://recipes.test:{Port}{path}";

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    public static Task WriteAsync(Stream stream, string status, IEnumerable<(string Name, string Value)> headers, byte[] body, CancellationToken ct)
    {
        var head = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var (name, value) in headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("Connection: close\r\n\r\n");
        return WriteBytesAsync(stream, Encoding.ASCII.GetBytes(head.ToString()), body, ct);
    }

    private static async Task WriteBytesAsync(Stream stream, byte[] head, byte[] body, CancellationToken ct)
    {
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static Task Ok(Stream stream, string contentType, string body, CancellationToken ct) =>
        WriteAsync(stream, "200 OK", [("Content-Type", contentType), ("Content-Length", Encoding.UTF8.GetByteCount(body).ToString())], Encoding.UTF8.GetBytes(body), ct);

    public static Task Redirect(Stream stream, string location, CancellationToken ct, string status = "302 Found") =>
        WriteAsync(stream, status, [("Location", location), ("Content-Length", "0"), ("Set-Cookie", "session=secret; Path=/")], [], ct);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _stop.Token);
                if (request is null)
                {
                    return;
                }

                lock (_seen)
                {
                    _seen.Add(request);
                }

                await _handler(request, stream, _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away (limit reached, timeout): expected in these tests.
            }
        }
    }

    private static async Task<SeenRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                return null;
            }

            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. bytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var path = lines[0].Split(' ')[1];
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new SeenRequest(path, headers);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }
}

/// <summary>Answers from a table; an unknown name is "host not found".</summary>
internal sealed class FakeResolver(Dictionary<string, IPAddress[]> table) : IHostResolver
{
    private readonly List<string> _asked = [];

    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked)
            {
                return [.. _asked];
            }
        }
    }

    public Func<string, int, IPAddress[]?>? Override { get; set; }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        int count;
        lock (_asked)
        {
            _asked.Add(host);
            count = _asked.Count(h => h == host);
        }

        if (Override?.Invoke(host, count) is { } overridden)
        {
            return Task.FromResult(overridden);
        }

        return table.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>
/// TEST ONLY. The production policy plus: loopback addresses and any port, so the fetcher can reach a TestHttpServer.
/// Everything else stays as in production (private, link-local and metadata addresses remain blocked), which is what the
/// redirect tests rely on. Never registered outside tests.
/// </summary>
internal sealed class LoopbackAllowedPolicy : IFetchPolicy
{
    public bool IsPortAllowed(int port) => port is > 0 and < 65536;

    public bool IsAddressAllowed(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) || IpAddressRules.IsPublic(address);
}

internal static class ImportTestServices
{
    /// <summary>Makes <c>recipes.test</c> (and any extra names) resolve as given, and lets the fetcher reach loopback test servers.</summary>
    public static Action<IServiceCollection> Use(FakeResolver resolver, IFetchPolicy? policy = null) => services =>
    {
        services.RemoveAll<IHostResolver>();
        services.RemoveAll<IFetchPolicy>();
        services.RemoveAll<IWebPageFetcher>();
        services.AddSingleton<IHostResolver>(resolver);
        services.AddSingleton(policy ?? new LoopbackAllowedPolicy());
        services.AddSingleton<IWebPageFetcher, SafeWebPageFetcher>();
    };

    public static FakeResolver Resolver(params (string Host, string Ip)[] entries) =>
        new(entries.ToDictionary(e => e.Host, e => new[] { IPAddress.Parse(e.Ip) }));
}
