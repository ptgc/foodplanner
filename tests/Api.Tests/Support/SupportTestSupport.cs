using System.Net;
using System.Text.Json;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foodplanner.Api.Tests.Support;

/// <summary>Keeps every log event with its category, to prove the audit mirror (T-096).</summary>
internal sealed class CategoryLogCapture : ILoggerProvider
{
    public List<(string Category, string Message)> Events { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capture(CategoryLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Events)
            {
                owner.Events.Add((category, formatter(state, exception) + exception));
            }
        }
    }
}

/// <summary>One row of <c>SupportAuditEntries</c>, read with raw SQL.</summary>
internal sealed record AuditRow(
    Guid Id,
    DateTimeOffset At,
    Guid AdminUserId,
    string AdminEmail,
    Guid? SessionId,
    Guid? TargetHouseholdId,
    string? TargetHouseholdName,
    string Action,
    string? Mode,
    string? ReasonCategory,
    string? ReasonNote,
    string? Detail,
    string Outcome);

internal static class SupportHost
{
    public const string Note = "The only owner left and the family cannot sign in";

    public static string NewAdminEmail() => $"admin-{Guid.NewGuid():N}@example.test";

    /// <summary>A host where <paramref name="adminEmail"/> is the only system admin. Test endpoints for the household policies are installed too.</summary>
    public static AuthHost Create(
        string connectionString, string adminEmail, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] settings)
    {
        var configuration = new Dictionary<string, string?> { ["Support:SystemAdminEmails:0"] = adminEmail };
        foreach (var (key, value) in settings)
        {
            configuration[key] = value;
        }

        return AuthHost.Create(
            connectionString,
            configuration,
            configureServices: services =>
            {
                services.AddSingleton<IStartupFilter, PolicyTestEndpointsStartupFilter>();
                configure?.Invoke(services);
            });
    }

    /// <summary>A confirmed account with the given address, signed in.</summary>
    public static async Task<SignedInUser> SignInAsAsync(this AuthHost host, string email)
    {
        var user = await host.CreateUserAsync(email);
        var client = host.CreateClient();
        using var login = await client.LoginAsync(email, AuthHost.Password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return new SignedInUser(new AppUserRef(user.Id, email), client);
    }

    public static Task<HttpResponseMessage> StartAsync(
        this ApiClient client, Guid household, string mode = "Read", string category = "LostOwner", string? note = Note, string? password = null) =>
        client.PostAsync("/api/support/sessions", new { householdId = household, mode, reasonCategory = category, reasonNote = note, password });

    public static async Task<JsonElement> StartOkAsync(
        this ApiClient client, Guid household, string mode = "Read", string? password = null)
    {
        using var response = await client.StartAsync(household, mode, password: mode == "Write" ? password ?? AuthHost.Password : password);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.JsonAsync();
    }

    public static Task<HttpResponseMessage> SupportGetAsync(this ApiClient client, Guid household, string path) =>
        client.GetAsync($"/api/support/households/{household}{path}");

    /// <summary>Raw SQL, outside EF and its filters: every audit row of the admin, oldest first.</summary>
    public static async Task<List<AuditRow>> AuditRowsAsync(string connectionString, Guid? adminUserId = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, At, AdminUserId, AdminEmail, SessionId, TargetHouseholdId, TargetHouseholdName, Action, Mode, ReasonCategory, ReasonNote, Detail, Outcome "
            + "FROM SupportAuditEntries WHERE (@admin IS NULL OR AdminUserId = @admin) ORDER BY At, Id";
        command.Parameters.AddWithValue("@admin", (object?)adminUserId ?? DBNull.Value);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0),
                reader.GetDateTimeOffset(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12)));
        }

        return rows;
    }

    public static async Task<int> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await HouseholdScenario.ScalarAsync(connectionString, sql, parameters));

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An Owner with a household that has a recipe, a planned meal, a kid profile and an open Adult invite.</summary>
    public static async Task<SupportHousehold> NewHouseholdAsync(this AuthHost host, string name = "Hansen")
    {
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync(name);
        var adult = await host.JoinAsync(owner, household, "Adult");
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await owner.Client.CreateRecipeAsync(household);
        var week = "2026-03-02";
        using var entry = await owner.Client.PostAsync($"/api/households/{household}/meal-plans/{week}/entries", new { date = "2026-03-03", recipeId = recipe, servings = 4 });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var profile = await owner.Client.CreateProfileAsync(household, "Mia");
        var invite = await owner.Client.CreateInviteAsync(household, "Adult");
        using var open = await owner.Client.GetAsync($"/api/households/{household}/invites");
        var inviteId = (await open.JsonAsync()).EnumerateArray().First().GetProperty("id").GetGuid();
        return new SupportHousehold(household, name, owner, adult, child, recipe, profile, inviteId, week);
    }
}

internal sealed record SupportHousehold(
    Guid Id, string Name, SignedInUser Owner, SignedInUser Adult, SignedInUser Child, Guid Recipe, Guid Profile, Guid OpenInvite, string Week);
