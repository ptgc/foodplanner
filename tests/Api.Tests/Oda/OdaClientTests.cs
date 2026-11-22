using System.Reflection;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Features.ShoppingLists;
using Foodplanner.Api.Tests.Auth;
using Foodplanner.Api.Tests.Persistence;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Infrastructure.Oda;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foodplanner.Api.Tests.Oda;

/// <summary>T-053 without HTTP or a database: the Oda client against recorded fixtures, the flow cookie, and the endpoint table.</summary>
public sealed class OdaClientTests
{
    private static readonly IReadOnlyList<CartLine> Lines =
    [
        new(Guid.NewGuid(), "Rice"),
        new(Guid.NewGuid(), "Egg"),
        new(Guid.NewGuid(), "Kaffefilter"),
    ];

    private static ServiceProvider Services(FakeOda fake, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oda:ClientId"] = OdaHost.ClientId,
            ["Oda:WebOrigin"] = OdaHost.Origin,
        }.Concat(settings.Select(s => KeyValuePair.Create<string, string?>(s.Key, s.Value)))).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(new OdaInnerHandler(fake));
        services.AddOda();
        return services.BuildServiceProvider();
    }

    private static Task<OdaActionOutcome> RunAsync(ServiceProvider provider, CancellationToken cancellationToken = default) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<OdaCartAction>().RunAsync(
            Guid.NewGuid(), Guid.NewGuid(), new OdaSecret(OdaHost.Code), new OdaSecret(OdaOAuth.NewVerifier()), Lines, cancellationToken);

    // ---- Contract fixtures -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Contract_fixtures_deserialise_with_the_real_client()
    {
        var fake = new FakeOda();
        await using var provider = Services(fake);

        var outcome = await RunAsync(provider, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["Rice", "Egg"], outcome.Fill!.Added.Select(a => a.Name).ToArray());
        Assert.Equal("https://oda.com/no/cart/", outcome.Fill.Cart!.Url);
        Assert.Single(outcome.Fill.Cart.Messages);
        Assert.True(outcome.ConnectionClosed);
        // The metadata fixtures are the saved real documents: the client found their endpoints.
        Assert.Contains(fake.Requests, r => r.Url.AbsolutePath == "/.well-known/oauth-protected-resource/mcp");
        Assert.Contains(fake.Requests, r => r.Url.AbsolutePath == "/.well-known/oauth-authorization-server/o");
    }

    [Fact]
    public async Task Contract_fixture_likely_to_buy_deserialises_and_only_ids_are_used()
    {
        var fake = new FakeOda();
        fake.UsualProductIds.Add(1004);
        await using var provider = Services(fake);

        var outcome = await RunAsync(provider, TestContext.Current.CancellationToken);

        Assert.Equal(UsuallyBoughtStatus.Used, outcome.Fill!.UsuallyBought);
        Assert.Equal(1, outcome.Fill.UsuallyBoughtCount);
        Assert.Equal(["Fake Jasminris 1 kg", "Fake Egg 12 stk"], outcome.Fill.Added.Select(a => a.ProductName).ToArray());
        Assert.Single(fake.ToolCalls("likely_to_buy"));
        Assert.Equal(["intent"], fake.ToolCalls("likely_to_buy")[0].AsObject().Select(p => p.Key).ToArray()); // no paging, no other argument
    }

    [Theory]
    [InlineData("no-result", UsuallyBoughtStatus.Unavailable)]
    [InlineData("not-array", UsuallyBoughtStatus.Unavailable)]
    [InlineData("entry-no-id", UsuallyBoughtStatus.Used)]
    public async Task A_damaged_likely_to_buy_fixture_never_throws_or_fails_the_fill(string damage, UsuallyBoughtStatus expected)
    {
        var fake = new FakeOda { HistoryDamage = damage };
        fake.UsualProductIds.Add(1004);
        await using var provider = Services(fake);

        var outcome = await RunAsync(provider, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(expected, outcome.Fill!.UsuallyBought);
        Assert.Equal(expected == UsuallyBoughtStatus.Used ? 1 : 0, outcome.Fill.UsuallyBoughtCount); // entries without a valid id are ignored
        Assert.Equal(2, outcome.Fill.Added.Count);
    }

    [Theory]
    [InlineData("tool", "product_search")]
    [InlineData("tool", "manipulate_cart")]
    [InlineData("tool", "get_cart")]
    [InlineData("argument", "product_search.queries")]
    [InlineData("argument", "manipulate_cart.operations")]
    [InlineData("cart-field", "groups")]
    public async Task Removing_a_field_or_tool_the_client_needs_is_oda_unavailable_not_an_exception(string kind, string name)
    {
        var fake = new FakeOda();
        switch (kind)
        {
            case "tool":
                fake.OmitTools.Add(name);
                break;
            case "argument":
                fake.OmitArguments.Add(name);
                break;
            default:
                fake.OmitCartFields.Add(name);
                break;
        }

        await using var provider = Services(fake);

        var outcome = await RunAsync(provider, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.ConnectionClosed);
        Assert.NotEmpty(fake.RevokeRequests);
    }

    // ---- Cancellation ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("product_search")]
    [InlineData("manipulate_cart")]
    [InlineData("get_cart")]
    public async Task Token_is_revoked_when_the_request_is_cancelled_mid_action(string cancelAt)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeOda { BeforeTool = name => { if (name == cancelAt) { cancellation.Cancel(); } } };
        await using var provider = Services(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(provider, cancellation.Token));

        Assert.Contains(fake.RevokeRequests, r => OdaHost.Form(r.Body)["token"] == fake.AccessToken);
        Assert.False(fake.RevokeTokenWasCancelled); // not the request's token
        Assert.True(fake.RevokeHadOwnTimeout); // its own timeout instead
    }

    // ---- Nothing is kept ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_instance_state_survives_a_run()
    {
        var fake = new FakeOda();
        await using var provider = Services(fake);

        var first = await RunAsync(provider, TestContext.Current.CancellationToken);
        var searchesAfterFirst = fake.ToolCalls("product_search").Count;
        var second = await RunAsync(provider, TestContext.Current.CancellationToken);

        // A second run searches again (nothing cached) and gets a fresh token.
        Assert.Equal(searchesAfterFirst * 2, fake.ToolCalls("product_search").Count);
        Assert.Equal(2, fake.TokenRequests.Count);
        Assert.Equal(first.Fill!.Added.Select(a => a.ProductName), second.Fill!.Added.Select(a => a.ProductName));
        Assert.Same(provider.GetRequiredService<OdaOAuth>(), provider.GetRequiredService<OdaOAuth>());

        // The singletons hold only their services and options: no collection or token in any field of the Oda types.
        foreach (var type in new[] { typeof(OdaOAuth), typeof(OdaHttp), typeof(OdaMcpSessionFactory), typeof(OdaCartAction), typeof(OdaShoppingListExporter) })
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var isStorage = field.FieldType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(field.FieldType);
                Assert.False(isStorage, $"{type.Name}.{field.Name} is a collection");
                var holdsSecret = field.FieldType == typeof(OdaSecret) || field.FieldType == typeof(OdaTokens);
                Assert.False(holdsSecret && type != typeof(OdaTokens) && type != typeof(OdaMcpSession), $"{type.Name}.{field.Name} holds a secret");
            }
        }
    }

    // ---- Flow cookie -------------------------------------------------------------------------------------------------

    private static OdaFlowCookie Cookie(ManualTimeProvider time) =>
        new(DataProtectionProvider.Create("oda-flow-tests"), time);

    private static (DefaultHttpContext Http, string Value) Issue(OdaFlowCookie cookie, OdaFlowState state)
    {
        var issued = new DefaultHttpContext();
        cookie.Issue(issued.Response, state);
        var header = issued.Response.Headers.SetCookie.ToString();
        var value = header[(OdaFlowCookie.CookieName.Length + 1)..].Split(';')[0];
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = $"{OdaFlowCookie.CookieName}={value}";
        return (next, value);
    }

    [Fact]
    public void Flow_cookie_is_bound_to_user_household_week_state_and_single_use()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var cookie = Cookie(time);
        var user = Guid.NewGuid();
        var household = Guid.NewGuid();
        var week = new DateOnly(2026, 10, 26);
        var state = new OdaFlowState("verifier-value", "state-value", household, week, user, time.GetUtcNow().Add(OdaFlowCookie.Lifetime));

        Assert.Null(cookie.Take(Issue(cookie, state).Http, "state-value", household, week, Guid.NewGuid())); // another user
        Assert.Null(cookie.Take(Issue(cookie, state).Http, "state-value", Guid.NewGuid(), week, user)); // another household
        Assert.Null(cookie.Take(Issue(cookie, state).Http, "state-value", household, week.AddDays(7), user)); // another week
        Assert.Null(cookie.Take(Issue(cookie, state).Http, "other", household, week, user)); // another state
        Assert.Null(cookie.Take(Issue(cookie, state).Http, null, household, week, user));

        var (http, value) = Issue(cookie, state);
        http.Request.Headers.Cookie = $"{OdaFlowCookie.CookieName}={value}x"; // tampered
        Assert.Null(cookie.Take(http, "state-value", household, week, user));

        var (good, _) = Issue(cookie, state);
        var taken = cookie.Take(good, "state-value", household, week, user);
        Assert.Equal("verifier-value", taken?.Verifier);
        Assert.Contains(OdaFlowCookie.CookieName + "=;", good.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal); // deleted
        Assert.Null(cookie.Take(Issue(cookie, state).Http, "state-value", household, week, user)); // replay

        var (late, _) = Issue(cookie, state with { State = "fresh" });
        time.Now = time.Now.AddMinutes(11);
        Assert.Null(cookie.Take(late, "fresh", household, week, user)); // expired
    }

    [Fact]
    public void Flow_cookie_value_does_not_contain_the_verifier_or_state()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cookie = Cookie(time);
        var (_, value) = Issue(cookie, new OdaFlowState("a-recognisable-verifier", "a-recognisable-state", Guid.NewGuid(), new DateOnly(2026, 10, 26), Guid.NewGuid(), time.GetUtcNow().AddMinutes(10)));

        Assert.DoesNotContain("recognisable", value, StringComparison.Ordinal);
    }

    // ---- Endpoint table ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Oda_endpoints_policies()
    {
        await using var host = AuthHost.Create(connectionString: string.Empty);
        {
            var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText!.Contains("/oda/", StringComparison.Ordinal))
                .ToList();

            Assert.Equal(
                ["/api/households/{householdId:guid}/shopping-lists/{weekStart}/oda/cart", "/api/households/{householdId:guid}/shopping-lists/{weekStart}/oda/start"],
                endpoints.Select(e => e.RoutePattern.RawText).Order().ToArray());
            foreach (var endpoint in endpoints)
            {
                var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
                Assert.Contains(Policies.HouseholdAdult, policies);
                Assert.Contains(Policies.FullSession, policies);
                Assert.NotNull(endpoint.Metadata.GetMetadata<Foodplanner.Api.Security.AntiforgeryValidatedMetadata>());
            }
        }
    }
}
