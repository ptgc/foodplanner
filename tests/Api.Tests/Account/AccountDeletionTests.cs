using System.Net;
using System.Text.Json;
using Foodplanner.Api.Realtime;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;
using Foodplanner.Api.Tests.Profiles;
using Foodplanner.Api.Tests.TenantIsolation;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Identity;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Foodplanner.Api.Tests.Account;

/// <summary>Deleting your own account (T-095, Q-027): preview, refusal, solo households, history kept, sessions, abuse limits.</summary>
public sealed class AccountDeletionTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private const string Monday = "2030-01-07";

    private AuthHost Host(
        RecordingImageStore? store = null, Action<IServiceCollection>? configure = null, params (string Key, string Value)[] settings) =>
        KidHost.Create(
            db.ConnectionString,
            services =>
            {
                // Never the real blob store: deleting a household calls it, and no storage runs in this test host.
                services.AddSingleton<IImageStore>(store ?? new RecordingImageStore());
                configure?.Invoke(services);
            },
            settings);

    private static string Url(Guid household, string rest = "") => $"/api/households/{household}{rest}";

    private static object DeleteBody(Guid[]? households = null, string? password = AuthHost.Password, string? confirmation = null, SignedInUser? user = null) =>
        new { password, confirmation = confirmation ?? user?.User.Email, householdIds = households ?? [] };

    private static Task<HttpResponseMessage> DeleteAsync(SignedInUser user, params Guid[] households) =>
        user.Client.PostAsync("/api/account/delete", DeleteBody(households, user: user));

    private static async Task<JsonElement> PreviewAsync(ApiClient client)
    {
        using var response = await client.GetAsync("/api/account/deletion-preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private static Guid[] Ids(JsonElement array) => [.. array.EnumerateArray().Select(h => h.GetProperty("id").GetGuid())];

    private async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(await HouseholdScenario.ScalarAsync(db.ConnectionString, sql, parameters));

    private Task<long> UsersAsync(Guid id) => CountAsync("SELECT COUNT(*) FROM AspNetUsers WHERE Id = @id", ("@id", id));

    private Task<long> HouseholdsAsync(Guid id) => CountAsync("SELECT COUNT(*) FROM Households WHERE Id = @id", ("@id", id));

    /// <summary>The second Owner (the one who stays) and the leaver, both Owners of one household.</summary>
    private static async Task<(SignedInUser Stays, SignedInUser Leaves, Guid Household)> TwoOwnersAsync(AuthHost host)
    {
        var stays = await host.SignInAsync();
        var household = await stays.Client.CreateHouseholdAsync("Shared");
        var leaves = await host.JoinAsync(stays, household, "Adult");
        using var promote = await stays.Client.PutAsync(Url(household, $"/members/{leaves.User.Id}/role"), new { role = "Owner" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        return (stays, leaves, household);
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task Preview_lists_households_that_go_with_the_account_block_it_and_live_on()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var solo = await user.Client.CreateHouseholdAsync("Solo");
        var blocking = await user.Client.CreateHouseholdAsync("Mine with guests");
        await host.JoinAsync(user, blocking, "Adult");
        var (stays, _, shared) = await TwoOwnersAsync(host);
        var other = await host.SignInAsync();
        var otherHousehold = await other.Client.CreateHouseholdAsync("Not mine");
        var guestOf = await host.SignInAsync();
        var kept = await guestOf.Client.CreateHouseholdAsync("Guest here");
        var token = await guestOf.Client.CreateInviteAsync(kept, "Adult");
        using (var accepted = await user.Client.AcceptAsync(token))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var preview = await PreviewAsync(user.Client);

        Assert.False(preview.GetProperty("canDelete").GetBoolean());
        Assert.Equal(user.User.Email, preview.GetProperty("confirmationText").GetString());
        Assert.Equal([solo], Ids(preview.GetProperty("householdsToDelete")));
        Assert.Equal([blocking], Ids(preview.GetProperty("blockingHouseholds")));
        Assert.Equal([kept], Ids(preview.GetProperty("householdsKept")));
        Assert.Equal(2, preview.GetProperty("blockingHouseholds")[0].GetProperty("memberCount").GetInt32());
        Assert.DoesNotContain(otherHousehold, Ids(preview.GetProperty("householdsKept")));
        Assert.DoesNotContain(shared, Ids(preview.GetProperty("blockingHouseholds")));
        Assert.NotNull(stays);
    }

    [Fact]
    public async Task Preview_can_delete_when_nothing_blocks()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var preview = await PreviewAsync(user.Client);
        Assert.True(preview.GetProperty("canDelete").GetBoolean());
        Assert.Empty(Ids(preview.GetProperty("householdsToDelete")));
    }

    [Fact]
    public async Task Preview_and_delete_need_a_session()
    {
        await using var host = Host();
        using var anonymous = host.CreateClient();
        using var preview = await anonymous.GetAsync("/api/account/deletion-preview");
        await preview.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        using var delete = await anonymous.PostAsync("/api/account/delete", DeleteBody(confirmation: "x@example.com"));
        await delete.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    // ---------------------------------------------------------------- refusal

    [Fact]
    public async Task Refused_while_the_only_owner_of_a_household_with_other_members_and_allowed_after_a_transfer()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync("Family");
        var adult = await host.JoinAsync(owner, household, "Adult");

        using var refused = await DeleteAsync(owner);
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "account-owns-households");
        var blocking = (await refused.JsonAsync()).GetProperty("households");
        Assert.Equal(household, blocking[0].GetProperty("id").GetGuid());
        Assert.Equal(1, await UsersAsync(owner.User.Id));
        Assert.Equal(1, await HouseholdsAsync(household));
        using (var still = await owner.Client.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        }

        // Transferring ownership (T-048) lifts the block; the household lives on with its other members.
        using var promote = await owner.Client.PutAsync(Url(household, $"/members/{adult.User.Id}/role"), new { role = "Owner" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        using var deleted = await DeleteAsync(owner);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await UsersAsync(owner.User.Id));
        Assert.Equal(1, await HouseholdsAsync(household));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Memberships WHERE HouseholdId = @h", ("@h", household)));
        var members = await ListMembersAsync(adult.Client, household);
        Assert.Equal([adult.User.Id], members);
    }

    private static async Task<Guid[]> ListMembersAsync(ApiClient client, Guid household)
    {
        using var response = await client.GetAsync(Url(household, "/members"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.JsonAsync()).EnumerateArray().Select(m => m.GetProperty("userId").GetGuid())];
    }

    [Fact]
    public async Task A_household_whose_only_other_member_is_a_child_still_blocks()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        await host.JoinAsync(owner, household, "Child");
        using var refused = await DeleteAsync(owner);
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "account-owns-households");
    }

    [Fact]
    public async Task Refused_when_the_households_to_delete_are_not_the_ones_confirmed()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var solo = await user.Client.CreateHouseholdAsync("Solo");

        using var none = await DeleteAsync(user);
        await none.AssertProblemAsync(HttpStatusCode.Conflict, "account-deletion-changed");
        Assert.Equal(solo, (await none.JsonAsync()).GetProperty("households")[0].GetProperty("id").GetGuid());
        using var wrong = await DeleteAsync(user, solo, Guid.NewGuid());
        await wrong.AssertProblemAsync(HttpStatusCode.Conflict, "account-deletion-changed");
        Assert.Equal(1, await HouseholdsAsync(solo));
        Assert.Equal(1, await UsersAsync(user.User.Id));
    }

    // ---------------------------------------------------------------- solo households and the account

    [Fact]
    public async Task Deletes_solo_households_with_everything_in_them_and_the_account_and_leaves_other_users_alone()
    {
        var store = new RecordingImageStore();
        await using var host = Host(store);
        var user = await host.SignInAsync();
        var solo = await user.Client.CreateHouseholdAsync("Solo");
        var otherSolo = await user.Client.CreateHouseholdAsync("Second solo");
        await user.Client.SetPinAsync();
        await user.Client.CreateProfileAsync(solo);
        var recipe = await user.Client.CreateRecipeAsync(solo);
        using (var pick = await user.Client.PostAsync(Url(solo, $"/meal-plans/{Monday}/entries"), new { date = Monday, recipeId = recipe }))
        {
            Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        }

        var bystander = await host.SignInAsync();
        var bystanderHousehold = await bystander.Client.CreateHouseholdAsync("Bystanders");
        var bystanderRecipe = await bystander.Client.CreateRecipeAsync(bystanderHousehold);
        using (var pick = await bystander.Client.PostAsync(
                   Url(bystanderHousehold, $"/meal-plans/{Monday}/entries"), new { date = Monday, recipeId = bystanderRecipe }))
        {
            Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        }

        using var response = await DeleteAsync(user, solo, otherSolo);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await UsersAsync(user.User.Id));
        Assert.Equal(0, await HouseholdsAsync(solo) + await HouseholdsAsync(otherSolo));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Memberships WHERE UserId = @u", ("@u", user.User.Id)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM UserPins WHERE UserId = @u", ("@u", user.User.Id)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM MemberProfiles WHERE HouseholdId = @h", ("@h", solo)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", solo)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM MealPlanEntries WHERE HouseholdId = @h", ("@h", solo)));
        Assert.Contains(RecipeImageBlobNames.HouseholdPrefix(solo), store.DeletedPrefixes);
        Assert.Contains(RecipeImageBlobNames.HouseholdPrefix(otherSolo), store.DeletedPrefixes);

        // Somebody else's account and data are untouched.
        Assert.Equal(1, await UsersAsync(bystander.User.Id));
        Assert.Equal(1, await HouseholdsAsync(bystanderHousehold));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Recipes WHERE HouseholdId = @h", ("@h", bystanderHousehold)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM MealPlanEntries WHERE HouseholdId = @h", ("@h", bystanderHousehold)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Memberships WHERE UserId = @u", ("@u", bystander.User.Id)));
        using var plan = await bystander.Client.GetAsync(Url(bystanderHousehold, $"/meal-plans/{Monday}"));
        Assert.Equal(bystander.User.Id, (await plan.JsonAsync()).GetProperty("entries")[0].GetProperty("pickedBy").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task A_solo_household_with_a_row_of_every_entity_type_is_removed_completely()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var solo = await user.Client.CreateHouseholdAsync("Everything");
        var types = TenantIsolationEnvironment.ProductionModel().GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && t.ClrType != typeof(Membership))
            .Select(t => t.ClrType).ToList();
        var env = new TenantIsolationEnvironment(db.ConnectionString);
        foreach (var type in types)
        {
            await TenantIsolationRegistry.Cases.Single(c => c.EntityType == type).SeedRowAsync(env, solo);
        }

        using var response = await DeleteAsync(user, solo);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await HouseholdsAsync(solo));
        foreach (var type in TenantIsolationEnvironment.ProductionModel().GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            var table = $"[{type.GetSchema() ?? "dbo"}].[{type.GetTableName()}]";
            Assert.True(
                await CountAsync($"SELECT COUNT(*) FROM {table} WHERE HouseholdId = @h", ("@h", solo)) == 0,
                $"{type.ClrType.Name} rows of the deleted household remain.");
        }
    }

    // ---------------------------------------------------------------- history in households that live on

    [Fact]
    public async Task History_stays_with_no_user_invites_are_revoked_and_drafts_are_deleted()
    {
        await using var host = Host();
        var (stays, leaves, household) = await TwoOwnersAsync(host);
        var recipe = await stays.Client.CreateRecipeAsync(household);

        // The leaver picks a meal, checks off an item, confirms a pantry item, locks the week, makes an invite and two imports.
        using var pick = await leaves.Client.PostAsync(Url(household, $"/meal-plans/{Monday}/entries"), new { date = Monday, recipeId = recipe });
        Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        var pickId = (await pick.JsonAsync()).GetProperty("id").GetGuid();
        using var manual = await leaves.Client.PostAsync(Url(household, $"/shopping-lists/{Monday}/items"), new { label = "Milk" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        var itemId = (await manual.JsonAsync()).GetProperty("id").GetGuid();
        using (var check = await leaves.Client.PutAsync(Url(household, $"/shopping-lists/{Monday}/items/{itemId}/check"), new { @checked = true }))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        using (var pantry = await leaves.Client.PutAsync(Url(household, $"/pantry/{KidHost.Salt}"), new { }))
        {
            Assert.True(pantry.IsSuccessStatusCode, pantry.StatusCode.ToString());
        }

        using (var locked = await leaves.Client.PutAsync(Url(household, $"/meal-plans/{Monday}/lock")))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        }

        var leaversToken = await leaves.Client.CreateInviteAsync(household, "Adult");
        var staysToken = await stays.Client.CreateInviteAsync(household, "Adult");
        var draft = await CreateImportAsync(leaves.Client, household);
        var staysDraft = await CreateImportAsync(stays.Client, household);
        var finished = await CreateImportAsync(leaves.Client, household);
        using (var discard = await leaves.Client.DeleteAsync(Url(household, $"/imports/{finished}")))
        {
            Assert.True(discard.IsSuccessStatusCode);
        }

        // The leaver also joined through the Owner's invite, which records them as the acceptor.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Invites WHERE AcceptedByUserId = @u", ("@u", leaves.User.Id)));

        using var response = await DeleteAsync(leaves);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(0, await UsersAsync(leaves.User.Id));
        Assert.Equal(1, await HouseholdsAsync(household));

        // The pick, the checked item, the pantry row and the lock stay; the person is gone.
        using var plan = await stays.Client.GetAsync(Url(household, $"/meal-plans/{Monday}"));
        var planJson = await plan.JsonAsync();
        var entry = planJson.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == pickId);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("pickedBy").GetProperty("userId").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("pickedBy").GetProperty("email").ValueKind);
        Assert.True(planJson.GetProperty("locked").GetBoolean());
        using var list = await stays.Client.GetAsync(Url(household, $"/shopping-lists/{Monday}"));
        var item = (await list.JsonAsync()).GetProperty("main").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId);
        Assert.True(item.GetProperty("checked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("checkedByUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("checkedByEmail").ValueKind);
        using var pantryList = await stays.Client.GetAsync(Url(household, "/pantry"));
        var pantryRow = (await pantryList.JsonAsync()).EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, pantryRow.GetProperty("confirmedByUserId").ValueKind);

        // Invites: the leaver's open one is revoked (not listed, not redeemable), the other Owner's still works.
        using var open = await stays.Client.GetAsync(Url(household, "/invites"));
        var openInvites = (await open.JsonAsync()).EnumerateArray().ToList();
        Assert.Single(openInvites);
        Assert.Equal(stays.User.Id, openInvites[0].GetProperty("createdByUserId").GetGuid());
        var newcomer = await host.SignInAsync();
        using (var revoked = await newcomer.Client.AcceptAsync(leaversToken))
        {
            await revoked.AssertProblemAsync(HttpStatusCode.Gone, "invite-invalid");
        }

        using (var works = await newcomer.Client.AcceptAsync(staysToken))
        {
            Assert.Equal(HttpStatusCode.OK, works.StatusCode);
        }

        // Imports: the leaver's draft is deleted, the finished one stays with no creator, the Owner's draft is untouched.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i", ("@i", draft)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND CreatedByUserId IS NULL", ("@i", finished)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM RecipeImports WHERE Id = @i AND CreatedByUserId = @u", ("@i", staysDraft), ("@u", stays.User.Id)));

        // Nothing in the database points at the deleted account.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Invites WHERE CreatedByUserId = @u OR AcceptedByUserId = @u", ("@u", leaves.User.Id)));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM MealPlans WHERE LockedByUserId = @u", ("@u", leaves.User.Id)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM MealPlans WHERE HouseholdId = @h AND LockedByUserId IS NULL AND LockedAt IS NOT NULL", ("@h", household)));
    }

    private static async Task<Guid> CreateImportAsync(ApiClient client, Guid household)
    {
        using var response = await client.PostAsync(
            Url(household, "/imports"), new { text = "Stekt noe\n\nIngredienser\n2 dl melk\n\nFremgangsmåte\nStek." });
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_leaves_and_the_household_lives_on()
    {
        await using var host = Host();
        var owner = await host.SignInAsync();
        var household = await owner.Client.CreateHouseholdAsync();
        var child = await host.JoinAsync(owner, household, "Child");
        var recipe = await owner.Client.CreateRecipeAsync(household);
        using (var pick = await child.Client.PostAsync(Url(household, $"/meal-plans/{Monday}/entries"), new { date = Monday, recipeId = recipe }))
        {
            Assert.Equal(HttpStatusCode.Created, pick.StatusCode);
        }

        using var response = await DeleteAsync(child);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await HouseholdsAsync(household));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM MealPlanEntries WHERE HouseholdId = @h AND PickedByUserId IS NULL", ("@h", household)));
        Assert.Equal([owner.User.Id], await ListMembersAsync(owner.Client, household));
    }

    // ---------------------------------------------------------------- sessions

    [Fact]
    public async Task Every_session_of_the_account_is_signed_out()
    {
        var live = new RecordingLiveConnections();
        await using var host = Host(configure: services =>
        {
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
            services.AddSingleton<ILiveConnections>(live);
        });
        var first = await host.SignInAsync();
        using var phone = host.CreateClient();
        using (var login = await phone.LoginAsync(first.User.Email, AuthHost.Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        using (var before = await phone.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var response = await DeleteAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The cookie of the deleting browser is cleared, and the other device's cookie is rejected: the user does not exist.
        using var own = await first.Client.GetAsync("/api/auth/me");
        await own.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        using var other = await phone.GetAsync("/api/auth/me");
        await other.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        using var relogin = await phone.LoginAsync(first.User.Email, AuthHost.Password);
        await relogin.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        Assert.Contains(first.User.Id, live.ClosedUsers);
    }

    private sealed class RecordingLiveConnections : ILiveConnections
    {
        public List<Guid> ClosedUsers { get; } = [];

        public Task EvictUserFromHouseholdAsync(Guid userId, Guid householdId) => Task.CompletedTask;

        public Task CloseHouseholdAsync(Guid householdId) => Task.CompletedTask;

        public Task CloseUserAsync(Guid userId)
        {
            ClosedUsers.Add(userId);
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- proof of identity and abuse limits

    [Fact]
    public async Task Wrong_password_and_missing_fields_delete_nothing()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        var solo = await user.Client.CreateHouseholdAsync();

        using var wrong = await user.Client.PostAsync("/api/account/delete", DeleteBody([solo], "not the password", user.User.Email));
        await wrong.AssertProblemAsync(HttpStatusCode.Forbidden, "password-incorrect");
        using var missingSecret = await user.Client.PostAsync("/api/account/delete", DeleteBody([solo], null, user.User.Email));
        Assert.Equal(HttpStatusCode.BadRequest, missingSecret.StatusCode);
        Assert.True((await missingSecret.JsonAsync()).GetProperty("errors").TryGetProperty("password", out _));
        using var noConfirmation = await user.Client.PostAsync("/api/account/delete", DeleteBody([solo], AuthHost.Password, "delete"));
        Assert.Equal(HttpStatusCode.BadRequest, noConfirmation.StatusCode);
        Assert.True((await noConfirmation.JsonAsync()).GetProperty("errors").TryGetProperty("confirmation", out _));
        using var noBody = await user.Client.PostAsync("/api/account/delete");
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        Assert.Equal(1, await UsersAsync(user.User.Id));
        Assert.Equal(1, await HouseholdsAsync(solo));

        // The typed confirmation ignores case and surrounding spaces.
        using var ok = await user.Client.PostAsync("/api/account/delete", DeleteBody([solo], AuthHost.Password, $"  {user.User.Email.ToUpperInvariant()} "));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_like_sign_in_does()
    {
        await using var host = Host();
        var user = await host.SignInAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await user.Client.PostAsync("/api/account/delete", DeleteBody(password: "wrong " + attempt, confirmation: user.User.Email));
            await wrong.AssertProblemAsync(HttpStatusCode.Forbidden, "password-incorrect");
        }

        // Locked: even the right password is refused, here and at sign-in (one shared counter).
        using var locked = await user.Client.PostAsync("/api/account/delete", DeleteBody(confirmation: user.User.Email));
        await locked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.True(locked.Headers.RetryAfter is not null);
        using var other = host.CreateClient();
        using var login = await other.LoginAsync(user.User.Email, AuthHost.Password);
        await login.AssertProblemAsync(HttpStatusCode.Unauthorized, "login-failed");
        Assert.Equal(1, await UsersAsync(user.User.Id));

        // After the lockout ends the right password works.
        host.Time.Now = host.Time.Now.AddMinutes(16);
        using var ok = await user.Client.PostAsync("/api/account/delete", DeleteBody(confirmation: user.User.Email));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task The_endpoint_is_rate_limited_per_client_and_shares_the_per_email_budget_of_sign_in()
    {
        await using var perClient = Host(settings: ("RateLimiting:AccountDelete:PermitLimit", "2"));
        var user = await perClient.SignInAsync();
        for (var i = 0; i < 2; i++)
        {
            using var wrong = await user.Client.PostAsync("/api/account/delete", DeleteBody(password: "no", confirmation: user.User.Email));
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using var limited = await user.Client.PostAsync("/api/account/delete", DeleteBody(confirmation: user.User.Email));
        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.Equal(1, await UsersAsync(user.User.Id));

        // Sign-in already used the whole per-email budget of this address, so deleting is refused too.
        await using var shared = Host(settings: ("RateLimiting:LoginByEmail:PermitLimit", "1"));
        var second = await shared.SignInAsync();
        using var refused = await second.Client.PostAsync("/api/account/delete", DeleteBody(confirmation: second.User.Email));
        await refused.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate-limited");
        Assert.Equal(1, await UsersAsync(second.User.Id));
    }

    // ---------------------------------------------------------------- kid mode

    [Fact]
    public async Task Kid_mode_is_refused_for_the_preview_and_the_delete()
    {
        await using var host = Host();
        var scenario = await host.NewScenarioAsync();
        await scenario.Owner.Client.EnterKidModeAsync(scenario.Household, scenario.Profile1);

        using var preview = await scenario.Owner.Client.GetAsync("/api/account/deletion-preview");
        await preview.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        using var delete = await scenario.Owner.Client.PostAsync("/api/account/delete", DeleteBody(user: scenario.Owner));
        await delete.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
        Assert.Equal(1, await UsersAsync(scenario.Owner.User.Id));
        Assert.Equal(1, await HouseholdsAsync(scenario.Household));
    }

    // ---------------------------------------------------------------- model guard

    [Fact]
    public void Every_foreign_key_to_a_user_is_handled_by_the_account_deletion()
    {
        // A new link to AspNetUsers must be decided here (and in ITenantResolution.EraseUserReferences) or the account
        // can no longer be deleted: the user keys are NO ACTION unless listed as cascading.
        var cascading = new[] { "Membership.UserId", "UserPin.UserId" };
        var nullable = new[]
        {
            "Invite.CreatedByUserId", "Invite.AcceptedByUserId", "MealPlan.LockedByUserId", "MealPlanEntry.PickedByUserId",
            "PantryItem.ConfirmedByUserId", "ShoppingListItem.CheckedByUserId", "RecipeImport.CreatedByUserId",
        };
        var identity = new[] { "IdentityUserClaim", "IdentityUserLogin", "IdentityUserToken" };
        var links = TenantIsolationEnvironment.ProductionModel().GetEntityTypes()
            .SelectMany(t => t.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(AppUser) && !identity.Any(i => fk.DeclaringEntityType.ClrType.Name.StartsWith(i, StringComparison.Ordinal)))
            .Select(fk => (Name: $"{fk.DeclaringEntityType.ClrType.Name}.{fk.Properties.Single().Name}", fk))
            .ToList();

        Assert.Equal(
            [.. cascading.Concat(nullable).Order(StringComparer.Ordinal)],
            [.. links.Select(l => l.Name).Order(StringComparer.Ordinal)]);
        foreach (var (name, fk) in links)
        {
            if (nullable.Contains(name))
            {
                Assert.False(fk.Properties.Single().IsNullable is false, $"{name} must be nullable so history can outlive the account.");
            }
        }
    }
}
