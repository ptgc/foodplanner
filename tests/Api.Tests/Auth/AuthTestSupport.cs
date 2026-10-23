using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Infrastructure.Email;
using Foodplanner.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Auth;

/// <summary>Class fixture: one migrated database per test class. Hosts are cheap and created per test.</summary>
public sealed class AuthDatabase(SqlServerFixture server) : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await server.CreateDatabaseAsync("auth", TestContext.Current.CancellationToken);
        await using var context = TenancyDatabase.CreateProductionContext(ConnectionString);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Counts password verifications and delegates to the real hasher.</summary>
public sealed class SpyPasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _inner = new();
    private int _verifications;

    public int Verifications => Volatile.Read(ref _verifications);

    public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verifications);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

/// <summary>Sets the connection's remote IP from a header, because TestServer has none. Used to test IP partitioning.</summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var value) && IPAddress.TryParse(value.ToString(), out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class AuthHost : IAsyncDisposable
{
    public const string Password = "correct horse battery";

    private readonly WebApplicationFactory<Program> _factory;

    private AuthHost(WebApplicationFactory<Program> factory, ManualTimeProvider time, SpyPasswordHasher hasher)
    {
        _factory = factory;
        Time = time;
        Hasher = hasher;
    }

    public ManualTimeProvider Time { get; }

    public SpyPasswordHasher Hasher { get; }

    public IServiceProvider Services => _factory.Services;

    public RecordingEmailSender Email => Services.GetRequiredService<RecordingEmailSender>();

    /// <summary>Limits are high by default so only the rate-limit tests hit them.</summary>
    public static AuthHost Create(
        string connectionString,
        IDictionary<string, string?>? configuration = null,
        string environment = "Development")
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var hasher = new SpyPasswordHasher();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Email:Provider"] = "Recording",
            ["RateLimiting:Auth:PermitLimit"] = "1000",
            ["RateLimiting:LoginByEmail:PermitLimit"] = "1000",
        };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<IPasswordHasher<AppUser>>();
                services.AddSingleton<IPasswordHasher<AppUser>>(hasher);
                services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>();
            });
        });
        return new AuthHost(factory, time, hasher);
    }

    public ApiClient CreateClient(string? remoteIp = null, bool useHttp = false) => new(_factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(useHttp ? "http://localhost" : "https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    }))
    { RemoteIp = remoteIp };

    /// <summary>Creates a user straight in the store, as an already registered account.</summary>
    public async Task<AppUser> CreateUserAsync(string email, bool confirmed = true, string password = Password)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { Id = Guid.NewGuid(), UserName = email, Email = email, EmailConfirmed = confirmed };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    public async Task<AppUser> FindUserAsync(string email)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return (await users.FindByEmailAsync(email))!;
    }

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>An HTTP client with a cookie jar that fetches a fresh antiforgery token before each unsafe request.</summary>
public sealed class ApiClient(HttpClient http) : IDisposable
{
    public string? RemoteIp { get; set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<HttpResponseMessage> GetAsync(string url, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        AddRemoteIp(request);
        return await http.SendAsync(request, Ct);
    }

    public async Task<string> GetTokenAsync()
    {
        using var response = await GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Posts with a freshly fetched token (tokens are bound to the current identity).</summary>
    public async Task<HttpResponseMessage> PostAsync(string url, object? body = null, IDictionary<string, string>? headers = null)
    {
        var token = await GetTokenAsync();
        return await SendPostAsync(url, body, token, headers);
    }

    /// <summary>Posts with the given token; null sends no antiforgery header at all.</summary>
    public async Task<HttpResponseMessage> PostWithTokenAsync(string url, object? body, string? token)
        => await SendPostAsync(url, body, token, null);

    public Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        PostAsync("/api/auth/login", new { email, password });

    private async Task<HttpResponseMessage> SendPostAsync(string url, object? body, string? token, IDictionary<string, string>? headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        AddRemoteIp(request);
        return await http.SendAsync(request, Ct);
    }

    private void AddRemoteIp(HttpRequestMessage request)
    {
        if (RemoteIp is not null)
        {
            request.Headers.Add(RemoteIpStartupFilter.Header, RemoteIp);
        }
    }

    public void Dispose() => http.Dispose();
}

internal static class ResponseExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    public static IReadOnlyList<string> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    public static async Task AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.JsonAsync();
        Assert.Equal($"urn:foodplanner:problem:{code}", body.GetProperty("type").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }
}
