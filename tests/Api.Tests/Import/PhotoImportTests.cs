using System.Net;
using System.Text.Json;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.Recipes;
using Foodplanner.Domain.Imports;
using Foodplanner.Infrastructure.Import.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Foodplanner.Api.Tests.Import;

/// <summary>Recipe from a photo (T-118): the endpoints, the draft in the existing import flow, validation, roles and tenancy. Claude is always a fake.</summary>
public sealed class PhotoImportTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}/imports{rest}";

    // ---- Roles, tenancy, policies ----------------------------------------------------------------------------------------

    [Fact]
    public async Task PhotoImport_roles()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var s = await host.NewScenarioAsync();
        var adult = await host.JoinAsync(s.Owner, s.Household, "Adult");
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        var stranger = await host.SignInAsync();
        await stranger.Client.CreateHouseholdAsync();

        // Owner and Adult succeed on all three.
        foreach (var who in new[] { s.Owner, adult })
        {
            var (_, id) = await PhotoHost.ImportOkAsync(who.Client, s.Household);
            using var usage = await who.Client.GetAsync(Url(s.Household, "/photo-usage"));
            Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
            Assert.True((await usage.JsonAsync()).GetProperty("enabled").GetBoolean());
            using var picture = await who.Client.GetAsync(Url(s.Household, $"/{id}/picture"));
            Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode); // no picture in this answer, but the role is accepted
        }

        var calls = reader.Calls;
        foreach (var who in new[] { child, stranger })
        {
            using var post = await PhotoHost.PostPhotosAsync(who.Client, s.Household);
            await post.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var usage = await who.Client.GetAsync(Url(s.Household, "/photo-usage"));
            await usage.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var picture = await who.Client.GetAsync(Url(s.Household, $"/{Guid.NewGuid()}/picture"));
            await picture.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        // A kid-mode session: the call itself needs a full session (kid-mode), the reads are for adults only.
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);
        using (var post = await PhotoHost.PostPhotosAsync(s.Owner.Client, s.Household))
        {
            await post.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        }

        using (var usage = await s.Owner.Client.GetAsync(Url(s.Household, "/photo-usage")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, usage.StatusCode);
        }

        using (var picture = await s.Owner.Client.GetAsync(Url(s.Household, $"/{Guid.NewGuid()}/picture")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, picture.StatusCode);
        }

        // Anonymous.
        using var anonymous = host.CreateClient();
        using var anonymousPost = await PhotoHost.PostPhotosAsync(anonymous, s.Household);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPost.StatusCode);
        using var anonymousUsage = await anonymous.GetAsync(Url(s.Household, "/photo-usage"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUsage.StatusCode);

        Assert.Equal(calls, reader.Calls); // no call to Claude in any denied case
    }

    [Fact]
    public async Task PhotoImport_other_household()
    {
        var reader = FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box()));
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (userA, householdA) = await PhotoHost.NewHouseholdAsync(host);
        var (userB, householdB) = await PhotoHost.NewHouseholdAsync(host); // another user, another household

        // userB (Owner of B) calls under household A: 403 on all three, no call.
        using (var post = await PhotoHost.PostPhotosAsync(userB.Client, householdA))
        {
            await post.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var usage = await userB.Client.GetAsync(Url(householdA, "/photo-usage")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, usage.StatusCode);
        }

        using (var picture = await userB.Client.GetAsync(Url(householdA, $"/{Guid.NewGuid()}/picture")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, picture.StatusCode);
        }

        Assert.Equal(0, reader.Calls);

        // A's import (with a picture) is not reachable through B's route by a user who is an Adult in both.
        var both = await host.SignInAsync();
        using (var accept = await both.Client.AcceptAsync(await userA.Client.CreateInviteAsync(householdA, "Adult")))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        using (var accept = await both.Client.AcceptAsync(await userB.Client.CreateInviteAsync(householdB, "Adult")))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        var (body, id) = await PhotoHost.ImportOkAsync(both.Client, householdA);
        Assert.NotNull(body.GetProperty("suggestedImage").GetProperty("url").GetString());
        using (var picture = await both.Client.GetAsync(Url(householdB, $"/{id}/picture")))
        {
            await picture.AssertProblemAsync(HttpStatusCode.NotFound, "import-not-found");
        }

        using (var get = await both.Client.GetAsync(Url(householdB, $"/{id}")))
        {
            await get.AssertProblemAsync(HttpStatusCode.NotFound, "import-not-found");
        }

        using (var own = await both.Client.GetAsync(Url(householdA, $"/{id}/picture")))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }
    }

    [Fact]
    public async Task PhotoImport_policies()
    {
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering());
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        static List<string?> PoliciesOf(RouteEndpoint e) => [.. e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)];
        const string Imports = "/api/households/{householdId:guid}/imports";

        var post = Assert.Single(endpoints, e => e.RoutePattern.RawText == Imports + "/photo");
        Assert.Contains(Policies.HouseholdAdult, PoliciesOf(post));
        Assert.Contains(Policies.FullSession, PoliciesOf(post));
        foreach (var rest in new[] { "/photo-usage", "/{importId:guid}/picture" })
        {
            var get = Assert.Single(endpoints, e => e.RoutePattern.RawText == Imports + rest);
            Assert.Contains(Policies.HouseholdAdult, PoliciesOf(get));
        }
    }

    // ---- The draft in the existing flow -----------------------------------------------------------------------------------

    [Fact]
    public async Task PhotoImport_creates_draft_in_existing_flow()
    {
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering());
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var recipesBefore = await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household);

        var (body, id) = await PhotoHost.ImportOkAsync(owner.Client, household, 2, "Skattekista");
        Assert.Equal("Photo", body.GetProperty("source").GetString());
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        Assert.Equal("Applied", body.GetProperty("aiAssist").GetString());
        Assert.Equal("Skattekista", body.GetProperty("sourceName").GetString()); // the typed name wins over the page
        Assert.Equal(49, body.GetProperty("photoUsage").GetProperty("remainingToday").GetInt32());
        Assert.Equal(50, body.GetProperty("photoUsage").GetProperty("dailyLimit").GetInt32());
        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.GetProperty("aiSuggested").GetBoolean()));
        Assert.Equal("Matched", lines[0].GetProperty("status").GetString());
        Assert.Equal("torskeloin", lines[0].GetProperty("note").GetString());
        Assert.Equal("UnmatchedIngredient", lines[2].GetProperty("status").GetString());
        Assert.Equal("1 dl kryptonittsaus", lines[2].GetProperty("original").GetString());
        Assert.Equal(recipesBefore, await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household)); // nothing saved yet

        using (var one = await owner.Client.GetAsync(Url(household, $"/{id}")))
        {
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        }

        using (var list = await owner.Client.GetAsync(Url(household)))
        {
            Assert.Contains((await list.JsonAsync()).EnumerateArray(), i => i.GetProperty("id").GetGuid() == id && i.GetProperty("source").GetString() == "Photo");
        }

        // Saving is blocked until the unmatched line is fixed; removing it is one of the existing review edits.
        using (var blocked = await owner.Client.PostAsync(Url(household, $"/{id}/save")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        }

        using (var edit = await owner.Client.PutAsync(Url(household, $"/{id}"), new { lines = new[] { new { position = 2, remove = true } }, sourceName = "Skattekista 2" }))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Equal("Skattekista 2", (await edit.JsonAsync()).GetProperty("sourceName").GetString());
        }

        using var saved = await owner.Client.PostAsync(Url(household, $"/{id}/save"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var recipeId = (await saved.JsonAsync()).GetProperty("recipeId").GetGuid();
        using var recipe = await owner.Client.GetAsync($"/api/households/{household}/recipes/{recipeId}");
        var detail = await recipe.JsonAsync();
        Assert.Equal("Skattekista 2", detail.GetProperty("sourceName").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sourceUrl").ValueKind);
        Assert.Equal("Kokkeknep: bruk dobbel folie.", detail.GetProperty("description").GetString());
        var ingredients = detail.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal(2, ingredients.Count);
        Assert.All(ingredients, i => Assert.NotEqual(Guid.Empty, i.GetProperty("ingredientId").GetGuid()));
        Assert.Equal(1, await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household) - recipesBefore);
    }

    [Fact]
    public async Task PhotoImport_nothing_saved_without_confirmation()
    {
        var store = new MemoryImageStore();
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(PhotoCanned.Recipe(PhotoCanned.Box())), store);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var recipes = await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household);
        var ingredients = await PhotoHost.CountAsync(db.ConnectionString, "Ingredients", household);

        var (_, id) = await PhotoHost.ImportOkAsync(owner.Client, household);
        Assert.Equal(recipes, await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household));
        Assert.Equal(ingredients, await PhotoHost.CountAsync(db.ConnectionString, "Ingredients", household));
        Assert.Single(store.Blobs); // the crop
        Assert.NotNull(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT RawContent FROM RecipeImports WHERE Id = @i", ("@i", id)) as string);

        using var discarded = await owner.Client.DeleteAsync(Url(household, $"/{id}"));
        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        Assert.Equal(DBNull.Value, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT RawContent FROM RecipeImports WHERE Id = @i", ("@i", id)));
        Assert.Equal(DBNull.Value, await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT DraftJson FROM RecipeImports WHERE Id = @i", ("@i", id)));
        Assert.Empty(store.Blobs);
        Assert.Equal(recipes, await PhotoHost.CountAsync(db.ConnectionString, "Recipes", household));
    }

    // ---- Validation ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PhotoImport_validation()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("Images:MaxUploadBytes", "200000"), ("Images:MaxDimension", "3000"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var good = PhotoFiles.Jpeg();

        async Task Rejected(MultipartFormDataContent form, string key)
        {
            using var response = await PhotoHost.PostFormAsync(owner.Client, household, form);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.JsonAsync()).GetProperty("errors");
            Assert.True(errors.TryGetProperty(key, out _), $"expected an error on {key}: {errors}");
        }

        await Rejected(PhotoFiles.Form([]), "photos");
        await Rejected(PhotoFiles.Form(Enumerable.Repeat(good, 5)), "photos");
        await Rejected(PhotoFiles.Form([good], partName: "file"), "photos"); // wrong part name = no photos
        await Rejected(PhotoFiles.Form([good, "%PDF-1.4 not a photo"u8.ToArray()]), "photos[1]");
        await Rejected(PhotoFiles.Form([[.. "ftypheic"u8, .. new byte[64]]]), "photos[0]"); // HEIC
        await Rejected(PhotoFiles.Form(["GIF89a"u8.ToArray()]), "photos[0]");
        await Rejected(PhotoFiles.Form([TestImages.AnimatedWebp()]), "photos[0]");
        await Rejected(PhotoFiles.Form([good[..(good.Length / 2)]]), "photos[0]"); // corrupt (cut)
        await Rejected(PhotoFiles.Form([new byte[200_001]]), "photos[0]"); // over MaxUploadBytes
        await Rejected(PhotoFiles.Form([TestImages.Png(3200, 100)]), "photos[0]"); // over MaxDimension
        await Rejected(PhotoFiles.Form([good], new string('x', 101)), "sourceName");

        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
    }

    // ---- Disabled --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("AiPhotoImport:Enabled", "false")]
    [InlineData("AiImport:ApiKey", "")]
    public async Task PhotoImport_disabled(string key, string value)
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, (key, value));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        using var usage = await owner.Client.GetAsync(Url(household, "/photo-usage"));
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        Assert.False((await usage.JsonAsync()).GetProperty("enabled").GetBoolean());
        using var post = await PhotoHost.PostPhotosAsync(owner.Client, household);
        await post.AssertProblemAsync(HttpStatusCode.NotFound, "photo-import-disabled");
        Assert.Equal(0, reader.Calls);
    }

    // ---- Limits ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PhotoImport_daily_limit()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "2"));
        host.Time.Now = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var (other, otherHousehold) = await PhotoHost.NewHouseholdAsync(host);

        var (first, _) = await PhotoHost.ImportOkAsync(owner.Client, household);
        Assert.Equal(1, first.GetProperty("photoUsage").GetProperty("remainingToday").GetInt32());
        await PhotoHost.ImportOkAsync(owner.Client, household);
        using (var usage = await owner.Client.GetAsync(Url(household, "/photo-usage")))
        {
            Assert.Equal(0, (await usage.JsonAsync()).GetProperty("remainingToday").GetInt32());
        }

        var calls = reader.Calls;
        using (var third = await PhotoHost.PostPhotosAsync(owner.Client, household))
        {
            await third.AssertProblemAsync(HttpStatusCode.TooManyRequests, "photo-import-limit-reached");
            Assert.Equal("50400", third.Headers.GetValues("Retry-After").Single()); // 14 hours to the next UTC midnight
            var problem = await third.JsonAsync();
            Assert.Equal(0, problem.GetProperty("remainingToday").GetInt32());
            Assert.Equal(2, problem.GetProperty("dailyLimit").GetInt32());
        }

        Assert.Equal(calls, reader.Calls);
        await PhotoHost.ImportOkAsync(other.Client, otherHousehold); // another household is unaffected

        host.Time.Now = host.Time.Now.AddDays(1);
        await PhotoHost.ImportOkAsync(owner.Client, household); // the next UTC day allows again
    }

    [Fact]
    public async Task PhotoImport_counters_are_separate_from_line_help()
    {
        var reader = FakePhotoReader.Answering();
        var lineAi = new FakeRecipeLineAi(_ => []);
        await using var host = PhotoHost.Create(
            db.ConnectionString, reader, null,
            services =>
            {
                services.RemoveAll<IRecipeLineAi>();
                services.AddSingleton<IRecipeLineAi>(lineAi);
            },
            ("AiPhotoImport:DailyPerHousehold", "1"), ("AiImport:DailyCallsPerHousehold", "1"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        // A text import with an unresolved line uses the line-help budget of 1.
        using (var text = await owner.Client.PostAsync(Url(household), new { text = "Test\nIngredienser\n2 dl dragefrukt\nFremgangsmåte\n1. Rør." }))
        {
            Assert.Equal(HttpStatusCode.Created, text.StatusCode);
        }

        Assert.Equal(1, lineAi.Calls);
        await PhotoHost.ImportOkAsync(owner.Client, household); // still allowed: its own counter
        Assert.Equal(1, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
        Assert.Equal(1, (int)(await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT DayCalls FROM AiHouseholdUsages WHERE HouseholdId = @h", ("@h", household)))!);
    }

    [Fact]
    public async Task PhotoImport_global_cap()
    {
        var reader = FakePhotoReader.Answering();
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:GlobalMonthly", "3"), ("AiPhotoImport:DailyPerHousehold", "5"));
        host.Time.Now = new DateTimeOffset(2031, 3, 10, 10, 0, 0, TimeSpan.Zero); // a month nobody else uses in this database
        var households = new List<(SignedInUser Owner, Guid Household)>();
        for (var i = 0; i < 4; i++)
        {
            households.Add(await PhotoHost.NewHouseholdAsync(host));
        }

        for (var i = 0; i < 3; i++)
        {
            await PhotoHost.ImportOkAsync(households[i].Owner.Client, households[i].Household);
        }

        using (var fourth = await PhotoHost.PostPhotosAsync(households[3].Owner.Client, households[3].Household))
        {
            await fourth.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "photo-import-capacity");
            Assert.False(fourth.Headers.Contains("Retry-After"));
        }

        Assert.Equal(3, reader.Calls);
        Assert.Equal(0, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, households[3].Household)); // the reservation was given back

        host.Time.Now = new DateTimeOffset(2031, 4, 2, 10, 0, 0, TimeSpan.Zero);
        await PhotoHost.ImportOkAsync(households[3].Owner.Client, households[3].Household); // the next month allows again
    }

    [Fact]
    public async Task PhotoImport_concurrent_limit()
    {
        var reader = new FakePhotoReader(async (_, ct) =>
        {
            await Task.Delay(300, ct);
            return new PhotoReadResult(PhotoReadOutcome.Read, PhotoCanned.Recipe(), 10, 5, "end_turn");
        });
        await using var host = PhotoHost.Create(
            db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "2"), ("AiPhotoImport:MaxConcurrent", "20"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        await PhotoHost.ImportOkAsync(owner.Client, household); // one of two used: one remains

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var response = await PhotoHost.PostPhotosAsync(owner.Client, household, 1);
            return response.StatusCode;
        }));

        Assert.Equal(1, responses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(2, reader.Calls);
        Assert.Equal(2, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
    }

    [Fact]
    public async Task PhotoImport_reservation_not_spent_before_send()
    {
        var gate = new TaskCompletionSource();
        var reader = new FakePhotoReader(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new PhotoReadResult(PhotoReadOutcome.Read, PhotoCanned.Recipe(), 10, 5, "end_turn");
        });
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:MaxConcurrent", "1"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        // 400: nothing reserved.
        using (var bad = await PhotoHost.PostFormAsync(owner.Client, household, PhotoFiles.Form([])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        Assert.Equal(0, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));

        // 503 busy: one call is in flight (and reserved), the second finds no slot and does not reserve.
        var inFlight = Task.Run(() => PhotoHost.PostPhotosAsync(owner.Client, household, 1), Ct);
        for (var i = 0; i < 100 && reader.Calls == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(1, reader.Calls);
        using (var busy = await PhotoHost.PostPhotosAsync(owner.Client, household, 1))
        {
            await busy.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "photo-import-busy");
        }

        Assert.Equal(1, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
        gate.SetResult();
        using (var done = await inFlight)
        {
            Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        }

        // 409 import-limit (50 drafts): checked before the reservation.
        for (var i = 0; i < 49; i++)
        {
            using var text = await owner.Client.PostAsync(Url(household), new { text = $"Draft {i}\nIngredienser\n2 dl melk\nFremgangsmåte\n1. Rør." });
            Assert.Equal(HttpStatusCode.Created, text.StatusCode);
        }

        var before = await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household);
        using var full = await PhotoHost.PostPhotosAsync(owner.Client, household, 1);
        await full.AssertProblemAsync(HttpStatusCode.Conflict, "import-limit");
        Assert.Equal(before, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
    }

    [Fact]
    public async Task PhotoImport_rate_limited()
    {
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(), null, null, ("RateLimiting:PhotoImport:PermitLimit", "2"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        await PhotoHost.ImportOkAsync(owner.Client, household, 1);
        await PhotoHost.ImportOkAsync(owner.Client, household, 1);
        using var third = await PhotoHost.PostPhotosAsync(owner.Client, household, 1);
        await third.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(third.Headers.Contains("Retry-After"));
    }

    // ---- Errors ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PhotoReadOutcome.NotARecipe, HttpStatusCode.UnprocessableEntity, "photo-import-not-a-recipe")]
    [InlineData(PhotoReadOutcome.Unreadable, HttpStatusCode.UnprocessableEntity, "photo-import-unreadable")]
    [InlineData(PhotoReadOutcome.Refused, HttpStatusCode.UnprocessableEntity, "photo-import-refused")]
    [InlineData(PhotoReadOutcome.MaxTokens, HttpStatusCode.BadGateway, "photo-import-failed")]
    [InlineData(PhotoReadOutcome.Failed, HttpStatusCode.BadGateway, "photo-import-failed")]
    [InlineData(PhotoReadOutcome.Timeout, HttpStatusCode.GatewayTimeout, "photo-import-timeout")]
    public async Task PhotoImport_errors(PhotoReadOutcome outcome, HttpStatusCode status, string code)
    {
        var reader = FakePhotoReader.Outcome(outcome, 321, 45, outcome == PhotoReadOutcome.MaxTokens ? "max_tokens" : "end_turn");
        await using var host = PhotoHost.Create(db.ConnectionString, reader, null, null, ("AiPhotoImport:DailyPerHousehold", "3"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);

        using var response = await PhotoHost.PostPhotosAsync(owner.Client, household);
        await response.AssertProblemAsync(status, code);
        var problem = await response.JsonAsync();
        Assert.Equal(2, problem.GetProperty("remainingToday").GetInt32()); // the call was sent: one reservation is spent
        Assert.DoesNotContain(PhotoCanned.PageText, problem.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await PhotoHost.CountAsync(db.ConnectionString, "RecipeImports", household));
        Assert.Equal(1, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
    }

    [Fact]
    public async Task PhotoImport_a_slow_call_is_a_timeout_and_the_reader_exception_is_a_failure()
    {
        var slow = new FakePhotoReader(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new PhotoReadResult(PhotoReadOutcome.Read, PhotoCanned.Recipe(), 1, 1, "end_turn");
        });
        await using var host = PhotoHost.Create(db.ConnectionString, slow, null, null, ("AiPhotoImport:TimeoutSeconds", "1"));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        using (var response = await PhotoHost.PostPhotosAsync(owner.Client, household))
        {
            await response.AssertProblemAsync(HttpStatusCode.GatewayTimeout, "photo-import-timeout");
        }

        Assert.Equal(1, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
        Assert.Equal("Timeout", await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Outcome FROM AiCallLogs WHERE HouseholdId = @h", ("@h", household)));

        var broken = new FakePhotoReader((_, _) => throw new InvalidOperationException("upstream said: SECRET-BODY"));
        await using var host2 = PhotoHost.Create(db.ConnectionString, broken);
        var (owner2, household2) = await PhotoHost.NewHouseholdAsync(host2);
        using var failed = await PhotoHost.PostPhotosAsync(owner2.Client, household2);
        await failed.AssertProblemAsync(HttpStatusCode.BadGateway, "photo-import-failed");
        Assert.DoesNotContain("SECRET-BODY", await failed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(1, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household2));
    }

    [Fact]
    public async Task PhotoImport_a_reader_that_could_not_start_gives_the_reservation_back()
    {
        var reader = new FakePhotoReader((_, _) => throw new PhotoReaderNotReadyException(new InvalidOperationException("no client")));
        await using var host = PhotoHost.Create(db.ConnectionString, reader);
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        using var response = await PhotoHost.PostPhotosAsync(owner.Client, household);
        await response.AssertProblemAsync(HttpStatusCode.BadGateway, "photo-import-failed");
        Assert.Equal(0, await PhotoHost.PhotoDayCallsAsync(db.ConnectionString, household));
        Assert.Equal(0, await PhotoHost.CountAsync(db.ConnectionString, "AiCallLogs", household));
    }

    // ---- AiCallLog -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AiCallLog_tokens()
    {
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering(null, 4321, 765));
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        var (_, id) = await PhotoHost.ImportOkAsync(owner.Client, household, 3);

        Assert.Equal(1, await PhotoHost.CountAsync(db.ConnectionString, "AiCallLogs", household));
        async Task<object?> Col(string column) =>
            await HouseholdScenario.ScalarAsync(db.ConnectionString, $"SELECT {column} FROM AiCallLogs WHERE HouseholdId = @h", ("@h", household));
        Assert.Equal("PhotoImport", await Col("Kind"));
        Assert.Equal("claude-opus-5-5", await Col("Model"));
        Assert.Equal("medium", await Col("Effort"));
        Assert.Equal(RecipePhotoSkill.Version, await Col("SkillVersion"));
        Assert.Equal(3, await Col("PhotoCount"));
        Assert.Equal(4321, await Col("InputTokens"));
        Assert.Equal(765, await Col("OutputTokens"));
        Assert.Equal("end_turn", await Col("StopReason"));
        Assert.Equal("Ok", await Col("Outcome"));
        Assert.Equal(id, await Col("ImportId"));

        // A failing call: a row with null tokens.
        await using var failing = PhotoHost.Create(db.ConnectionString, new FakePhotoReader((_, _) => throw new InvalidOperationException("boom")));
        var (owner2, household2) = await PhotoHost.NewHouseholdAsync(failing);
        using var response = await PhotoHost.PostPhotosAsync(owner2.Client, household2);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            DBNull.Value,
            await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT InputTokens FROM AiCallLogs WHERE HouseholdId = @h", ("@h", household2)));
        Assert.Equal(
            "Failed",
            await HouseholdScenario.ScalarAsync(db.ConnectionString, "SELECT Outcome FROM AiCallLogs WHERE HouseholdId = @h", ("@h", household2)));
    }

    [Fact]
    public async Task AiCallLog_a_failure_to_write_the_row_does_not_fail_the_import()
    {
        // The row cannot be written: a model name the column rejects is cut by the entity, so break it another way, by dropping the table's
        // insert with a trigger that raises an error.
        await using var host = PhotoHost.Create(db.ConnectionString, FakePhotoReader.Answering());
        var (owner, household) = await PhotoHost.NewHouseholdAsync(host);
        await HouseholdScenario.ScalarAsync(
            db.ConnectionString,
            $"CREATE TRIGGER trg_block_{household:N} ON AiCallLogs AFTER INSERT AS BEGIN RAISERROR('blocked', 16, 1); ROLLBACK TRANSACTION; END");
        try
        {
            using var response = await PhotoHost.PostPhotosAsync(owner.Client, household);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(0, await PhotoHost.CountAsync(db.ConnectionString, "AiCallLogs", household));
        }
        finally
        {
            await HouseholdScenario.ScalarAsync(db.ConnectionString, $"DROP TRIGGER trg_block_{household:N}");
        }
    }
}
