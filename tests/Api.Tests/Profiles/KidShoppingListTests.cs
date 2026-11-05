using System.Net;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Households;

namespace Foodplanner.Api.Tests.Profiles;

/// <summary>
/// T-105: the shopping list and the pantry are read only in kid mode. Every write answers 403 <c>kid-mode</c> (policy
/// FullSession), the reads stay open, and a Child member with their own login outside kid mode keeps today's rights.
/// </summary>
public sealed class KidShoppingListTests(AuthDatabase db) : IClassFixture<AuthDatabase>
{
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private AuthHost Host() => KidHost.Create(db.ConnectionString);

    private static string H(Guid household) => $"/api/households/{household}";

    private static string ListUrl(Guid household, string rest = "") => $"{H(household)}/shopping-lists/{Monday:yyyy-MM-dd}{rest}";

    /// <summary>Every shopping-list, chosen-store and pantry write, as (name, call). Item and ingredient ids are the ones given.</summary>
    private static (string Name, Func<ApiClient, Task<HttpResponseMessage>> Call)[] Writes(Guid h, Guid item, Guid store) =>
    [
        ("generate", c => c.PostAsync(ListUrl(h, "/generate"))),
        ("weekly", c => c.PostAsync(ListUrl(h, "/weekly"))),
        ("add item", c => c.PostAsync(ListUrl(h, "/items"), new { label = "Extra", quantity = (decimal?)null, unitId = (Guid?)null })),
        ("check", c => c.PutAsync(ListUrl(h, $"/items/{item}/check"), new { @checked = true })),
        ("uncheck", c => c.PutAsync(ListUrl(h, $"/items/{item}/check"), new { @checked = false })),
        ("delete item", c => c.DeleteAsync(ListUrl(h, $"/items/{item}"))),
        ("choose store", c => c.PutAsync($"{H(h)}/chosen-store", new { storeId = store })),
        ("pantry confirm", c => c.PutAsync($"{H(h)}/pantry/{KidHost.Salt}", new { })),
        ("pantry remove", c => c.DeleteAsync($"{H(h)}/pantry/{KidHost.Salt}")),
        ("pantry clear", c => c.DeleteAsync($"{H(h)}/pantry")),
    ];

    private async Task<(KidScenario S, SignedInUser Child, Guid Item, Guid Store)> ArrangeAsync(AuthHost host)
    {
        var s = await host.NewScenarioAsync();
        var child = await host.JoinAsync(s.Owner, s.Household, "Child");
        using var item = await s.Owner.Client.PostAsync(ListUrl(s.Household, "/items"), new { label = "Bread", quantity = (decimal?)null, unitId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        using var store = await s.Owner.Client.PostAsync($"{H(s.Household)}/stores", new { name = "Rema", sectionOrder = (string[]?)null });
        Assert.Equal(HttpStatusCode.Created, store.StatusCode);
        using var pantry = await s.Owner.Client.PutAsync($"{H(s.Household)}/pantry/{KidHost.Salt}", new { });
        Assert.Equal(HttpStatusCode.OK, pantry.StatusCode);
        return (s, child, (await item.JsonAsync()).GetProperty("id").GetGuid(), (await store.JsonAsync()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Every_shopping_list_and_pantry_write_is_refused_in_kid_mode_and_changes_nothing()
    {
        await using var host = Host();
        var (s, _, item, store) = await ArrangeAsync(host);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        foreach (var (name, call) in Writes(s.Household, item, store))
        {
            using var response = await call(s.Owner.Client);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "kid-mode");
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, name);
        }

        // Nothing moved: the item is still there and unchecked, the pantry still has salt, no store is chosen.
        using var list = await s.Owner.Client.GetAsync(ListUrl(s.Household));
        var items = (await list.JsonAsync()).GetProperty("main").EnumerateArray().ToList();
        var bread = Assert.Single(items);
        Assert.False(bread.GetProperty("checked").GetBoolean());
        using var pantry = await s.Owner.Client.GetAsync($"{H(s.Household)}/pantry");
        Assert.Single((await pantry.JsonAsync()).EnumerateArray());
        using var chosen = await s.Owner.Client.GetAsync($"{H(s.Household)}/chosen-store");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await chosen.JsonAsync()).GetProperty("storeId").ValueKind);
    }

    [Fact]
    public async Task Reads_stay_open_in_kid_mode()
    {
        await using var host = Host();
        var (s, _, _, _) = await ArrangeAsync(host);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        foreach (var url in new[] { ListUrl(s.Household), $"{H(s.Household)}/chosen-store", $"{H(s.Household)}/pantry", $"{H(s.Household)}/stores", $"{H(s.Household)}/weekly-groceries" })
        {
            using var response = await s.Owner.Client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} -> {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task A_child_member_outside_kid_mode_keeps_todays_rights()
    {
        await using var host = Host();
        var (s, child, item, store) = await ArrangeAsync(host);
        var c = child.Client;
        var h = s.Household;

        // The same calls kid mode refuses. Clearing the pantry is Adult-only, so a Child gets the plain 403 (unchanged).
        foreach (var (name, call) in Writes(h, item, store))
        {
            using var response = await call(c);
            if (name == "pantry clear")
            {
                await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            }
            else
            {
                Assert.True(response.IsSuccessStatusCode, $"{name} -> {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task Kid_mode_on_another_household_gets_the_non_member_answer_not_kid_mode()
    {
        await using var host = Host();
        var s = await host.NewScenarioAsync();
        var other = await host.SignInAsync();
        var b = await other.Client.CreateHouseholdAsync("Grandparents");
        using var accepted = await s.Owner.Client.AcceptAsync(await other.Client.CreateInviteAsync(b, "Adult"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await s.Owner.Client.EnterKidModeAsync(s.Household, s.Profile1);

        foreach (var (name, call) in Writes(b, Guid.NewGuid(), Guid.NewGuid()))
        {
            using var response = await call(s.Owner.Client);
            await response.AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, name);
        }
    }
}
