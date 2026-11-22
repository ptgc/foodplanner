using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Api.Tests.Support;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Persistence.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Tests.Import;

/// <summary>
/// T-123: the limiter reads the household's plan. Reservations run on the real limiter against SQL Server (one context per call, like
/// one per request), with a fake clock; the photo-usage and import endpoints are checked with a fake reader.
/// </summary>
public sealed class HouseholdPlanLimiterTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig(string connectionString, Guid household, ManualTimeProvider time, AiImportOptions lineHelp, AiPhotoImportOptions photo)
    {
        public ManualTimeProvider Time => time;

        public async Task<T> RunAsync<T>(Func<AiUsageLimiter, Task<T>> action)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
            await using var context = new AppDbContext(options, new FixedHousehold(household), new TenantWriteScope(), time);
            return await action(new AiUsageLimiter(context, time, Options.Create(lineHelp), Options.Create(photo)));
        }

        public Task<AiReservation> Photo() => RunAsync(l => l.TryReservePhotoAsync(Ct));

        public Task<AiReservation> Line() => RunAsync(l => l.TryReserveAsync(Ct));

        public Task<PhotoQuota> Quota() => RunAsync(l => l.PhotoQuotaAsync(Ct));

        public async Task<List<AiReservation>> Many(int count, Func<Rig, Task<AiReservation>> one)
        {
            var results = new List<AiReservation>();
            for (var i = 0; i < count; i++)
            {
                results.Add(await one(this));
            }

            return results;
        }
    }

    private Rig NewRig(Guid household, AiPhotoImportOptions? photo = null, AiImportOptions? lineHelp = null, ManualTimeProvider? time = null) =>
        new(db.ConnectionString, household, time ?? new ManualTimeProvider(Noon), lineHelp ?? new AiImportOptions(), photo ?? new AiPhotoImportOptions());

    private static async Task<Guid> NewHouseholdAsync(AuthHost host) => (await PhotoHost.NewHouseholdAsync(host)).Household;

    /// <summary>Raw SQL, like an admin's PUT would leave it. A null limit is unlimited.</summary>
    private async Task SetPlanAsync(Guid household, int? photo, int? lineDay, int? lineMonth, string? name = "Test") =>
        await SupportHost.ExecuteAsync(
            db.ConnectionString,
            "DELETE FROM HouseholdPlans WHERE HouseholdId = @h; INSERT INTO HouseholdPlans (Id, HouseholdId, Name, PhotoImportsPerDay, LineHelpPerDay, LineHelpPerMonth, CreatedAt, UpdatedAt) "
            + "VALUES (NEWID(), @h, @n, @p, @d, @m, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())",
            ("@h", household), ("@n", (object?)name ?? DBNull.Value), ("@p", (object?)photo ?? DBNull.Value), ("@d", (object?)lineDay ?? DBNull.Value), ("@m", (object?)lineMonth ?? DBNull.Value));

    private Task ClearPlanAsync(Guid household) =>
        SupportHost.ExecuteAsync(db.ConnectionString, "DELETE FROM HouseholdPlans WHERE HouseholdId = @h", ("@h", household));

    private Task<int> CounterAsync(Guid household, string column) =>
        SupportHost.CountAsync(db.ConnectionString, $"SELECT COALESCE(MAX({column}), 0) FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household));

    private static AuthHost Host(string connectionString) => AuthHost.Create(connectionString);

    // ---- Photo imports ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Photo_default_plan_allows_two_a_day()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "2"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        await PhotoHost.ImportOkAsync(owner.Client, household);
        await PhotoHost.ImportOkAsync(owner.Client, household);
        using (var third = await PhotoHost.PostPhotosAsync(owner.Client, household))
        {
            await third.AssertProblemAsync(HttpStatusCode.TooManyRequests, "photo-import-limit-reached");
        }

        using var usage = await owner.Client.GetAsync($"/api/households/{household}/imports/photo-usage");
        var body = await usage.JsonAsync();
        Assert.False(body.GetProperty("unlimited").GetBoolean());
        Assert.Equal(2, body.GetProperty("dailyLimit").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingToday").GetInt32());
    }

    [Fact]
    public async Task Photo_custom_limit_is_honoured()
    {
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        await SetPlanAsync(household, 5, null, null);
        var rig = NewRig(household);

        var results = await rig.Many(6, r => r.Photo());

        Assert.Equal(5, results.Count(r => r == AiReservation.Reserved));
        Assert.Equal(AiReservation.HouseholdLimit, results[5]);
        Assert.Equal(5, await CounterAsync(household, "PhotoDayCalls"));
    }

    [Fact]
    public async Task Photo_custom_limit_answers_429_with_the_plan_limit()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        await SetPlanAsync(household, 1, null, null);

        await PhotoHost.ImportOkAsync(owner.Client, household);
        using var second = await PhotoHost.PostPhotosAsync(owner.Client, household);

        await second.AssertProblemAsync(HttpStatusCode.TooManyRequests, "photo-import-limit-reached");
        var problem = await second.JsonAsync();
        Assert.Equal(1, problem.GetProperty("dailyLimit").GetInt32());
        Assert.Equal(0, problem.GetProperty("remainingToday").GetInt32());
    }

    [Fact]
    public async Task Photo_plan_of_zero_refuses_the_first_without_any_counter_write()
    {
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        await SetPlanAsync(household, 0, null, null);
        var rig = NewRig(household);

        Assert.Equal(AiReservation.HouseholdLimit, await rig.Photo());

        Assert.Equal(0, await SupportHost.CountAsync(db.ConnectionString, "SELECT COUNT(*) FROM AiHouseholdUsages WHERE HouseholdId = @h AND PhotoDayCalls > 0", ("@h", household)));
        Assert.Equal(0, (await rig.Quota()).Remaining);
    }

    [Fact]
    public async Task Photo_unlimited_skips_household_check()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "2"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        await SetPlanAsync(household, null, null, null, "Unlimited");
        var rig = NewRig(household, new AiPhotoImportOptions { DailyPerHousehold = 2 });

        var results = await rig.Many(50, r => r.Photo());

        Assert.All(results, r => Assert.Equal(AiReservation.Reserved, r));
        Assert.Equal(50, await CounterAsync(household, "PhotoDayCalls")); // the counters keep counting
        using var usage = await owner.Client.GetAsync($"/api/households/{household}/imports/photo-usage");
        var body = await usage.JsonAsync();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.True(body.GetProperty("unlimited").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("dailyLimit").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("remainingToday").ValueKind);

        var (imported, _) = await PhotoHost.ImportOkAsync(owner.Client, household); // the import itself is never refused by the household limit
        Assert.True(imported.GetProperty("photoUsage").GetProperty("unlimited").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, imported.GetProperty("photoUsage").GetProperty("remainingToday").ValueKind);
    }

    [Fact]
    public async Task Unlimited_does_not_skip_global_cap()
    {
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        await SetPlanAsync(household, null, null, null);
        var time = new ManualTimeProvider(new DateTimeOffset(2031, 3, 10, 12, 0, 0, TimeSpan.Zero)); // a month no other test counts in

        // Photos: the global cap of 3.
        var photoRig = NewRig(household, new AiPhotoImportOptions { GlobalMonthly = 3 }, time: time);
        var photos = await photoRig.Many(4, r => r.Photo());
        Assert.Equal(3, photos.Count(r => r == AiReservation.Reserved));
        Assert.Equal(AiReservation.GlobalLimit, photos[3]);
        Assert.Equal(3, await CounterAsync(household, "PhotoDayCalls")); // the refused one was given back

        // Line help: the same, with its own global counter (another month, so the photo counter does not interfere).
        var lineTime = new ManualTimeProvider(new DateTimeOffset(2031, 4, 10, 12, 0, 0, TimeSpan.Zero));
        var lineRig = NewRig(household, lineHelp: new AiImportOptions { GlobalMonthlyCalls = 3 }, time: lineTime);
        var lines = await lineRig.Many(4, r => r.Line());
        Assert.Equal(3, lines.Count(r => r == AiReservation.Reserved));
        Assert.Equal(AiReservation.GlobalLimit, lines[3]);

        // A global cap of 0 refuses an unlimited household.
        Assert.Equal(AiReservation.GlobalLimit, await NewRig(household, new AiPhotoImportOptions { GlobalMonthly = 0 }).Photo());
        Assert.Equal(AiReservation.GlobalLimit, await NewRig(household, lineHelp: new AiImportOptions { GlobalMonthlyCalls = 0 }).Line());
    }

    // ---- Line help -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Line_help_plan_limits()
    {
        await using var host = Host(db.ConnectionString);
        var time = new ManualTimeProvider(new DateTimeOffset(2032, 1, 10, 12, 0, 0, TimeSpan.Zero));

        // Day limit 1.
        var dayHousehold = await NewHouseholdAsync(host);
        await SetPlanAsync(dayHousehold, null, 1, null);
        var dayRig = NewRig(dayHousehold, time: time);
        Assert.Equal(AiReservation.Reserved, await dayRig.Line());
        Assert.Equal(AiReservation.HouseholdLimit, await dayRig.Line());
        time.Now = time.Now.AddDays(1); // a new UTC day restarts the count
        Assert.Equal(AiReservation.Reserved, await dayRig.Line());

        // Month limit 3, day not reached.
        var monthHousehold = await NewHouseholdAsync(host);
        await SetPlanAsync(monthHousehold, null, null, 3);
        var monthTime = new ManualTimeProvider(new DateTimeOffset(2032, 2, 10, 12, 0, 0, TimeSpan.Zero));
        var monthRig = NewRig(monthHousehold, time: monthTime);
        var results = await monthRig.Many(4, r => r.Line());
        Assert.Equal(3, results.Count(r => r == AiReservation.Reserved));
        Assert.Equal(AiReservation.HouseholdLimit, results[3]);
        monthTime.Now = new DateTimeOffset(2032, 3, 1, 0, 5, 0, TimeSpan.Zero); // a new UTC month restarts it
        Assert.Equal(AiReservation.Reserved, await monthRig.Line());

        // Unlimited for both: 30 calls pass although the defaults are 20 and 200.
        var open = await NewHouseholdAsync(host);
        await SetPlanAsync(open, null, null, null);
        var openRig = NewRig(open, time: new ManualTimeProvider(new DateTimeOffset(2032, 5, 10, 12, 0, 0, TimeSpan.Zero)));
        var all = await openRig.Many(30, r => r.Line());
        Assert.All(all, r => Assert.Equal(AiReservation.Reserved, r));
        Assert.Equal(30, await CounterAsync(open, "DayCalls"));
    }

    // ---- Tenancy, changes, concurrency, configuration ----------------------------------------------------------------------

    [Fact]
    public async Task Plan_is_per_household()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "2"));
        host.Time.Now = Noon; // the rigs below count in the same UTC day as the endpoints
        var (ownerA, a) = await PhotoHost.NewHouseholdAsync(host);
        var (ownerB, b) = await PhotoHost.NewHouseholdAsync(host);
        await SetPlanAsync(a, null, null, null);
        var photo = new AiPhotoImportOptions { DailyPerHousehold = 2 };
        var rigA = NewRig(a, photo);
        var rigB = NewRig(b, photo);

        var forA = await rigA.Many(5, r => r.Photo());
        var forB = await rigB.Many(3, r => r.Photo());

        Assert.All(forA, r => Assert.Equal(AiReservation.Reserved, r));
        Assert.Equal([AiReservation.Reserved, AiReservation.Reserved, AiReservation.HouseholdLimit], forB);
        Assert.Equal(0, await PlanRowsSeenAsync(db.ConnectionString, b)); // B's context never sees A's row
        using var usage = await ownerB.Client.GetAsync($"/api/households/{b}/imports/photo-usage");
        var body = await usage.JsonAsync();
        Assert.False(body.GetProperty("unlimited").GetBoolean());
        Assert.Equal(2, body.GetProperty("dailyLimit").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingToday").GetInt32());
        using var usageA = await ownerA.Client.GetAsync($"/api/households/{a}/imports/photo-usage");
        Assert.True((await usageA.JsonAsync()).GetProperty("unlimited").GetBoolean());
    }

    /// <summary>How many plan rows a context scoped to <paramref name="household"/> sees (never another household's).</summary>
    private static async Task<int> PlanRowsSeenAsync(string connectionString, Guid household)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options;
        await using var context = new AppDbContext(options, new FixedHousehold(household), new TenantWriteScope(), TimeProvider.System);
        return await context.HouseholdPlans.CountAsync(p => p.HouseholdId != household, Ct);
    }

    [Fact]
    public async Task Plan_change_applies_next_request_without_reset()
    {
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        var rig = NewRig(household, new AiPhotoImportOptions { DailyPerHousehold = 2 });
        await rig.Many(2, r => r.Photo());
        Assert.Equal(0, (await rig.Quota()).Remaining);

        await SetPlanAsync(household, 5, null, null); // raised
        var raised = await rig.Quota();
        Assert.Equal(5, raised.DailyLimit);
        Assert.Equal(3, raised.Remaining);

        await SetPlanAsync(household, 1, null, null); // lowered below what was used: no refund, no reset
        var lowered = await rig.Quota();
        Assert.Equal(1, lowered.DailyLimit);
        Assert.Equal(0, lowered.Remaining);
        Assert.Equal(AiReservation.HouseholdLimit, await rig.Photo());

        await ClearPlanAsync(household); // back to the configured default
        var reset = await rig.Quota();
        Assert.Equal(2, reset.DailyLimit);
        Assert.Equal(0, reset.Remaining);
        Assert.Equal(2, await CounterAsync(household, "PhotoDayCalls")); // no change touched the counter
    }

    [Fact]
    public async Task Concurrent_first_line_help_reservations_never_over_reserve()
    {
        // Verifier addition: the first-reservation race (no counter row yet) for line help, with a day limit of 4.
        await using var host = Host(db.ConnectionString);
        var limited = await NewHouseholdAsync(host);
        await SetPlanAsync(limited, null, 4, null);
        var open = await NewHouseholdAsync(host);
        await SetPlanAsync(open, null, null, null);
        var time = new ManualTimeProvider(new DateTimeOffset(2032, 5, 10, 12, 0, 0, TimeSpan.Zero)); // a month no other test counts in

        var limitedResults = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(limited, time: time).Line()));
        var openResults = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(open, time: time).Line()));

        Assert.Equal(4, limitedResults.Count(r => r == AiReservation.Reserved));
        Assert.Equal(6, limitedResults.Count(r => r == AiReservation.HouseholdLimit));
        Assert.Equal(4, await CounterAsync(limited, "DayCalls"));
        Assert.Equal(4, await CounterAsync(limited, "MonthCalls"));
        Assert.All(openResults, r => Assert.Equal(AiReservation.Reserved, r));
        Assert.Equal(10, await CounterAsync(open, "DayCalls"));
    }

    [Fact]
    public async Task Concurrent_first_reservations_respect_the_global_cap_for_an_unlimited_household()
    {
        // Verifier addition: the global month row does not exist yet, ten requests race to create it, the cap is 4.
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        await SetPlanAsync(household, null, null, null);
        var photoTime = new ManualTimeProvider(new DateTimeOffset(2032, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var lineTime = new ManualTimeProvider(new DateTimeOffset(2032, 7, 10, 12, 0, 0, TimeSpan.Zero));

        var photos = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(household, new AiPhotoImportOptions { GlobalMonthly = 4 }, time: photoTime).Photo()));
        Assert.Equal(4, photos.Count(r => r == AiReservation.Reserved));
        Assert.Equal(6, photos.Count(r => r == AiReservation.GlobalLimit));
        Assert.Equal(4, await CounterAsync(household, "PhotoDayCalls")); // every refused one was given back

        var lines = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(household, lineHelp: new AiImportOptions { GlobalMonthlyCalls = 4 }, time: lineTime).Line()));
        Assert.Equal(4, lines.Count(r => r == AiReservation.Reserved));
        Assert.Equal(6, lines.Count(r => r == AiReservation.GlobalLimit));
        Assert.Equal(4, await CounterAsync(household, "DayCalls"));
    }

    [Fact]
    public async Task Concurrent_reservations_respect_a_limited_plan()
    {
        await using var host = Host(db.ConnectionString);
        var limited = await NewHouseholdAsync(host);
        await SetPlanAsync(limited, 4, null, null);
        var open = await NewHouseholdAsync(host);
        await SetPlanAsync(open, null, null, null);

        var limitedResults = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(limited).Photo()));
        var openResults = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewRig(open).Photo()));

        Assert.Equal(4, limitedResults.Count(r => r == AiReservation.Reserved));
        Assert.Equal(6, limitedResults.Count(r => r == AiReservation.HouseholdLimit));
        Assert.Equal(4, await CounterAsync(limited, "PhotoDayCalls"));
        Assert.All(openResults, r => Assert.Equal(AiReservation.Reserved, r));
        Assert.Equal(10, await CounterAsync(open, "PhotoDayCalls"));
    }

    [Fact]
    public async Task Configuration_default_is_live()
    {
        await using var host = Host(db.ConnectionString);
        var followsDefault = await NewHouseholdAsync(host);
        var custom = await NewHouseholdAsync(host);
        await SetPlanAsync(custom, 2, null, null);

        Assert.Equal(2, (await NewRig(followsDefault, new AiPhotoImportOptions { DailyPerHousehold = 2 }).Quota()).DailyLimit);
        Assert.Equal(3, (await NewRig(followsDefault, new AiPhotoImportOptions { DailyPerHousehold = 3 }).Quota()).DailyLimit);
        var changed = NewRig(custom, new AiPhotoImportOptions { DailyPerHousehold = 3 });
        Assert.Equal(2, (await changed.Quota()).DailyLimit); // a custom plan ignores the configured default

        var results = await NewRig(followsDefault, new AiPhotoImportOptions { DailyPerHousehold = 3 }).Many(4, r => r.Photo());
        Assert.Equal(3, results.Count(r => r == AiReservation.Reserved));
    }

    [Fact]
    public async Task Disabled_feature_beats_the_plan()
    {
        foreach (var disabled in new[] { new[] { ("AiPhotoImport:Enabled", "false") }, new[] { ("AiImport:ApiKey", "") } })
        {
            var reader = FakePhotoReader.Answering();
            await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, disabled);
            var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
            await SetPlanAsync(household, null, null, null);

            using var usage = await owner.Client.GetAsync($"/api/households/{household}/imports/photo-usage");
            var body = await usage.JsonAsync();
            Assert.False(body.GetProperty("enabled").GetBoolean());
            Assert.False(body.GetProperty("unlimited").GetBoolean());
            using var post = await PhotoHost.PostPhotosAsync(owner.Client, household);
            await post.AssertProblemAsync(HttpStatusCode.NotFound, "photo-import-disabled");
            Assert.Equal(0, reader.Calls);
        }
    }

    [Fact]
    public async Task Home_owner_of_a_seeded_unlimited_plan_is_never_refused_by_the_household_limit()
    {
        await using var host = Host(db.ConnectionString);
        var household = await NewHouseholdAsync(host);
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantResolution>().EnsureHouseholdPlan(
                household, "Unlimited", new Foodplanner.Domain.Tenancy.HouseholdPlanLimits(
                    Foodplanner.Domain.Tenancy.AiLimit.Unlimited, Foodplanner.Domain.Tenancy.AiLimit.Unlimited, Foodplanner.Domain.Tenancy.AiLimit.Unlimited), Ct);
        }

        var results = await NewRig(household, new AiPhotoImportOptions { DailyPerHousehold = 2 }).Many(10, r => r.Photo());

        Assert.All(results, r => Assert.Equal(AiReservation.Reserved, r));
    }
}
