using Foodplanner.Api.Tests.Auth;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Import;

/// <summary>Hardening of the AI client registration (T-077): no redirects, https outside Development and Testing, stop reason logging.</summary>
public sealed class AiImportHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AiLineRequest Request = new(["2 dl dragefrukt"], ["dl"]);

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Test";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        map(app);
        await app.StartAsync(Ct);
        return app;
    }

    // ---- No redirects ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_is_unavailable_and_the_target_never_gets_a_request_or_the_key(int status)
    {
        var targetHits = 0;
        string? keyAtTarget = null;
        await using var target = await StartAsync(app => app.MapFallback(context =>
        {
            Interlocked.Increment(ref targetHits);
            keyAtTarget = context.Request.Headers["x-api-key"];
            return Task.CompletedTask;
        }));
        var targetUrl = target.Urls.First();
        await using var origin = await StartAsync(app => app.MapPost(
            "/v1/messages", (HttpContext context) =>
            {
                context.Response.StatusCode = status;
                context.Response.Headers.Location = targetUrl + "/stolen";
                return Task.CompletedTask;
            }));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new Env("Development"));
        services.Configure<AiImportOptions>(o =>
        {
            o.Enabled = true;
            o.ApiKey = "test-key-not-real";
            o.BaseUrl = origin.Urls.First();
        });
        services.AddAiImport();
        await using var provider = services.BuildServiceProvider();

        // The real registered client, over a real socket handler.
        var client = provider.GetRequiredService<IRecipeLineAi>();
        Assert.IsType<AnthropicRecipeLineAi>(client);

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.ParseAsync(Request, Ct));

        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref targetHits));
        Assert.Null(keyAtTarget);
    }

    // ---- https outside Development and Testing ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "http://api.example.com", true, false)]
    [InlineData("Staging", "http://api.example.com", true, false)]
    [InlineData("Production", "not a url", true, false)]
    [InlineData("Production", "ftp://api.example.com", true, false)]
    [InlineData("Production", "https://api.anthropic.com", true, true)]
    [InlineData("Development", "http://localhost:9999", true, true)]
    [InlineData("Testing", "http://localhost:9999", true, true)]
    [InlineData("Production", "http://api.example.com", false, true)] // Off: nothing is sent, so nothing to protect.
    public void The_base_url_must_be_https_when_active_outside_development_and_testing(string environment, string baseUrl, bool enabled, bool valid)
    {
        var options = new AiImportOptions { Enabled = enabled, ApiKey = enabled ? "k" : null, BaseUrl = baseUrl };

        var result = new AiImportOptionsValidator(new Env(environment)).Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("AiImport:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
            Assert.Contains("https", result.FailureMessage, StringComparison.Ordinal);
        }
    }

    private static void StartHost(string environment, string baseUrl)
    {
        using var factory = new TestApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = "Recording",
                ["AiImport:Enabled"] = "true",
                ["AiImport:ApiKey"] = "test-key-not-real",
                ["AiImport:BaseUrl"] = baseUrl,
            }));
        });
        using var client = factory.CreateClient();
    }

    [Fact]
    public void Startup_fails_in_production_with_a_plain_http_base_url_and_says_why()
    {
        var ex = Assert.ThrowsAny<Exception>(() => StartHost("Production", "http://api.example.com"));

        var text = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("AiImport:BaseUrl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-not-real", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_succeeds_in_production_with_https_and_in_development_with_http()
    {
        StartHost("Production", "https://api.anthropic.com");
        StartHost("Development", "http://localhost:9999");
    }

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
        }
    }

    // ---- stop_reason -------------------------------------------------------------------------------------------------------

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<(CaptureLogger<AnthropicRecipeLineAi> Log, Exception? Error)> AnswerAsync(string body)
    {
        var log = new CaptureLogger<AnthropicRecipeLineAi>();
        var client = new AnthropicRecipeLineAi(
            new HttpClient(new FuncHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            }))),
            Options.Create(new AiImportOptions { Enabled = true, ApiKey = "test-key-not-real" }),
            log);
        try
        {
            await client.ParseAsync(new AiLineRequest(["SECRET LINE TEXT"], ["dl"]), Ct);
            return (log, null);
        }
        catch (AiUnavailableException ex)
        {
            return (log, ex);
        }
    }

    [Fact]
    public async Task A_max_tokens_stop_reason_is_logged_without_line_text_even_when_the_answer_is_unreadable()
    {
        var (log, error) = await AnswerAsync("""{"content":[{"type":"text","text":"cut"}],"stop_reason":"max_tokens"}""");

        Assert.NotNull(error); // No tool block: unavailable, and the cause is visible in the log.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("token limit", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_stop_reasons_log_nothing()
    {
        var (log, error) = await AnswerAsync(
            """{"content":[{"type":"tool_use","name":"submit_ingredient_lines","input":{"lines":[]}}],"stop_reason":"tool_use"}""");

        Assert.Null(error);
        Assert.Empty(log.Entries);
        Assert.Null(AnthropicRecipeLineAi.StopReason("not json"));
        Assert.Null(AnthropicRecipeLineAi.StopReason("[1]"));
        Assert.Equal("max_tokens", AnthropicRecipeLineAi.StopReason("""{"stop_reason":"max_tokens"}"""));
    }
}

/// <summary>The limiter must not leave a new counter row tracked when the insert fails for a reason other than a duplicate (T-077).</summary>
public sealed class AiUsageLimiterInsertFailureTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private sealed class FailInsert : SaveChangesInterceptor
    {
        public bool Armed { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added))
            {
                throw new InvalidOperationException("insert failed");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_failed_insert_that_is_not_a_duplicate_leaves_nothing_tracked_so_a_later_save_cannot_insert_it()
    {
        var fail = new FailInsert();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(db.ConnectionString).AddInterceptors(fail).Options;
        var household = Guid.NewGuid();
        await using var context = new AppDbContext(options, new Persistence.FixedHousehold(household), new TenantWriteScope(), TimeProvider.System);
        var limiter = new AiUsageLimiter(context, TimeProvider.System, Options.Create(new AiImportOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => limiter.TryReserveAsync(TestContext.Current.CancellationToken));

        Assert.Empty(context.ChangeTracker.Entries<AiHouseholdUsage>());

        // The import's own SaveChanges comes next: with the guard off it must have nothing to write.
        fail.Armed = false;
        Assert.Equal(0, await context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
