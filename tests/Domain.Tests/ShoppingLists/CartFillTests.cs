using Foodplanner.Domain.ShoppingLists.Cart;

namespace Foodplanner.Domain.Tests.ShoppingLists;

public class CartFillTests
{
    private static ProductCandidate Hit(string id, bool available = true, string? name = null) => new(id, name ?? $"Product {id}", available);

    // ---- Selection ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Selection_keeps_unchecked_named_items_in_order_and_trims_names()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var lines = CartLineSelector.Select(
        [
            new CartSourceItem(a, " Rice ", false),
            new CartSourceItem(Guid.NewGuid(), "Checked", true),
            new CartSourceItem(Guid.NewGuid(), "   ", false),
            new CartSourceItem(b, "Egg", false),
            new CartSourceItem(c, "Kaffefilter", false),
        ]);

        Assert.Equal([a, b, c], lines.Select(l => l.ItemId).ToArray());
        Assert.Equal(["Rice", "Egg", "Kaffefilter"], lines.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void Selection_of_nothing_is_empty()
    {
        Assert.Empty(CartLineSelector.Select([]));
        Assert.Empty(CartLineSelector.Select([new CartSourceItem(Guid.NewGuid(), "x", true)]));
    }

    // ---- Matching ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_available_hit_wins()
    {
        var match = ProductMatcher.Match([Hit("1", available: false), Hit("2"), Hit("3")]);

        Assert.Equal(ProductMatchOutcome.Matched, match.Outcome);
        Assert.Equal("2", match.Product!.ProductId);
        Assert.Equal("1", ProductMatcher.Match([Hit("1"), Hit("2")]).Product!.ProductId);
    }

    [Fact]
    public void No_hits_is_no_match_and_only_unavailable_hits_is_unavailable()
    {
        Assert.Equal(ProductMatchOutcome.NoMatch, ProductMatcher.Match([]).Outcome);
        var unavailable = ProductMatcher.Match([Hit("1", false), Hit("2", false)]);
        Assert.Equal(ProductMatchOutcome.Unavailable, unavailable.Outcome);
        Assert.Null(unavailable.Product);
    }

    // ---- Usual matcher (T-114: Usual_matcher_rules) ---------------------------------------------------------------------

    private static IReadOnlySet<string> Usual(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_usual_hit_wins_over_an_earlier_available_hit()
    {
        var match = ProductMatcher.Match([Hit("1"), Hit("2"), Hit("3")], Usual("2"));

        Assert.Equal(ProductMatchOutcome.Matched, match.Outcome);
        Assert.Equal("2", match.Product!.ProductId);
        Assert.Equal(ProductMatchSource.UsuallyBought, match.Source);
    }

    [Fact]
    public void No_usual_hit_gives_the_first_available_hit_as_first_hit()
    {
        var match = ProductMatcher.Match([Hit("1", available: false), Hit("2"), Hit("3")], Usual("9"));

        Assert.Equal("2", match.Product!.ProductId);
        Assert.Equal(ProductMatchSource.FirstHit, match.Source);
    }

    [Fact]
    public void A_usual_hit_that_is_unavailable_is_skipped()
    {
        var match = ProductMatcher.Match([Hit("1"), Hit("2", available: false), Hit("3")], Usual("2"));

        Assert.Equal("1", match.Product!.ProductId);
        Assert.Equal(ProductMatchSource.FirstHit, match.Source);
        var next = ProductMatcher.Match([Hit("1"), Hit("2", available: false), Hit("3")], Usual("2", "3"));
        Assert.Equal("3", next.Product!.ProductId);
        Assert.Equal(ProductMatchSource.UsuallyBought, next.Source);
    }

    [Fact]
    public void Two_usual_hits_give_the_better_ranked_one_whatever_the_set_order()
    {
        Assert.Equal("2", ProductMatcher.Match([Hit("1"), Hit("2"), Hit("3")], Usual("3", "2")).Product!.ProductId);
        Assert.Equal("2", ProductMatcher.Match([Hit("1"), Hit("2"), Hit("3")], Usual("2", "3")).Product!.ProductId);
    }

    [Fact]
    public void A_usual_id_that_is_not_among_the_hits_is_never_chosen()
    {
        var match = ProductMatcher.Match([Hit("1"), Hit("2")], Usual("99"));

        Assert.Equal("1", match.Product!.ProductId);
        Assert.Equal(ProductMatchSource.FirstHit, match.Source);
        Assert.Equal(ProductMatchOutcome.NoMatch, ProductMatcher.Match([], Usual("99")).Outcome);
    }

    [Fact]
    public void Usual_ids_are_compared_exactly()
    {
        Assert.Equal(ProductMatchSource.FirstHit, ProductMatcher.Match([Hit("12")], Usual("1", "123", " 12")).Source);
    }

    [Fact]
    public void Usual_matching_keeps_no_match_and_unavailable_and_an_empty_set_is_the_old_rule()
    {
        Assert.Equal(ProductMatchOutcome.NoMatch, ProductMatcher.Match([], Usual("1")).Outcome);
        Assert.Equal(ProductMatchOutcome.Unavailable, ProductMatcher.Match([Hit("1", false), Hit("2", false)], Usual("1")).Outcome);
        var hits = new[] { Hit("1", false), Hit("2"), Hit("3") };
        Assert.Equal(ProductMatcher.Match(hits), ProductMatcher.Match(hits, Usual()));
        Assert.Equal(ProductMatchSource.FirstHit, ProductMatcher.Match(hits, Usual()).Source);
    }

    [Fact]
    public void Matcher_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => ProductMatcher.Match(null!));
        Assert.Throws<ArgumentNullException>(() => ProductMatcher.Match(null!, Usual()));
        Assert.Throws<ArgumentNullException>(() => ProductMatcher.Match([Hit("1")], null!));
    }

    // ---- Filling -----------------------------------------------------------------------------------------------------

    private sealed class FakeSession : ICartProviderSession
    {
        public List<IReadOnlyList<string>> Searches { get; } = [];

        public List<IReadOnlyList<string>> Adds { get; } = [];

        public int CartReads { get; private set; }

        public Func<string, IReadOnlyList<ProductCandidate>> Hits { get; set; } = q => [Hit(q + "-id")];

        public Func<string, bool> Takes { get; set; } = _ => true;

        public Func<int, Exception?> AddFailure { get; set; } = _ => null;

        public Exception? CartFailure { get; set; }

        public IReadOnlySet<string> Usual { get; set; } = new HashSet<string>();

        public Exception? HistoryFailure { get; set; }

        public int HistoryReads { get; private set; }

        public List<string> Calls { get; } = [];

        public Task<IReadOnlySet<string>> ReadUsuallyBoughtIdsAsync(CancellationToken cancellationToken)
        {
            HistoryReads++;
            Calls.Add("history");
            return HistoryFailure is null ? Task.FromResult(Usual) : Task.FromException<IReadOnlySet<string>>(HistoryFailure);
        }

        public Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
        {
            Searches.Add(queries);
            Calls.Add("search");
            return Task.FromResult<IReadOnlyList<IReadOnlyList<ProductCandidate>>>([.. queries.Select(Hits)]);
        }

        public Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken)
        {
            Adds.Add(productIds);
            Calls.Add("add");
            if (AddFailure(Adds.Count) is { } failure)
            {
                throw failure;
            }

            return Task.FromResult<IReadOnlySet<string>>(productIds.Where(Takes).ToHashSet());
        }

        public Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken)
        {
            CartReads++;
            return CartFailure is null ? Task.FromResult(new CartSnapshot("https://shop.example/cart", ["one message"])) : Task.FromException<CartSnapshot>(CartFailure);
        }
    }

    private static IReadOnlyList<CartLine> Items(int count) => [.. Enumerable.Range(1, count).Select(i => new CartLine(Guid.NewGuid(), $"item{i}"))];

    [Fact]
    public async Task Search_and_add_run_in_batches_of_ten_and_the_cart_is_read_once()
    {
        var session = new FakeSession();

        var result = await CartFiller.FillAsync(session, Items(25), CancellationToken.None);

        Assert.Equal([10, 10, 5], session.Searches.Select(s => s.Count).ToArray());
        Assert.Equal([10, 10, 5], session.Adds.Select(s => s.Count).ToArray());
        Assert.Equal(1, session.CartReads);
        Assert.Equal(25, result.Added.Count);
        Assert.Equal(CartFillStatus.Done, result.Status);
        Assert.Equal("https://shop.example/cart", result.Cart!.Url);
        Assert.All(result.Added, a => Assert.Equal(1, a.Quantity));
    }

    [Fact]
    public async Task Each_line_is_added_matched_or_reported_with_its_reason()
    {
        var session = new FakeSession
        {
            Hits = q => q switch
            {
                "none" => [],
                "soldout" => [Hit("s", false)],
                "refused" => [Hit("r")],
                "second" => [Hit("a", false), Hit("b", name: "Second hit")],
                _ => [Hit("ok")],
            },
            Takes = id => id != "r",
        };
        var lines = new[] { "ok", "none", "soldout", "refused", "second" }.Select(n => new CartLine(Guid.NewGuid(), n)).ToList();

        var result = await CartFiller.FillAsync(session, lines, CancellationToken.None);

        Assert.Equal(["ok", "second"], result.Added.Select(a => a.Name).ToArray());
        Assert.Equal("Second hit", result.Added.Single(a => a.Name == "second").ProductName);
        Assert.Equal(
            [("none", CartNotAddedReason.NoMatch), ("soldout", CartNotAddedReason.Unavailable), ("refused", CartNotAddedReason.Rejected)],
            result.NotAdded.OrderBy(n => n.Name == "none" ? 0 : n.Name == "soldout" ? 1 : 2).Select(n => (n.Name, n.Reason)).ToArray());
        Assert.Equal(CartFillStatus.Partial, result.Status);
        Assert.DoesNotContain("s", session.Adds.SelectMany(a => a).ToList()); // unavailable products are never sent
    }

    [Fact]
    public async Task Nothing_matched_is_nothing_added_and_sends_no_cart_call()
    {
        var session = new FakeSession { Hits = _ => [] };

        var result = await CartFiller.FillAsync(session, Items(3), CancellationToken.None);

        Assert.Equal(CartFillStatus.NothingAdded, result.Status);
        Assert.Empty(session.Adds);
    }

    [Fact]
    public async Task A_failed_add_is_not_repeated_and_reports_what_was_added_before()
    {
        var session = new FakeSession { AddFailure = call => call == 2 ? new ProviderUnavailableException("boom") : null };

        var ex = await Assert.ThrowsAsync<CartFillInterruptedException>(() => CartFiller.FillAsync(session, Items(25), CancellationToken.None));

        Assert.Equal(10, ex.Added.Count);
        Assert.Equal(2, session.Adds.Count); // the failed call was made once and never again
    }

    [Fact]
    public async Task A_search_failure_touches_no_cart()
    {
        var session = new FakeSession { Hits = _ => throw new ProviderUnavailableException("down") };

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => CartFiller.FillAsync(session, Items(3), CancellationToken.None));

        Assert.Empty(session.Adds);
    }

    [Fact]
    public async Task A_wrong_number_of_search_answers_fails_closed()
    {
        var session = new MismatchedSession();

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => CartFiller.FillAsync(session, Items(3), CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_cart_is_a_missing_link_not_a_failure()
    {
        var session = new FakeSession { CartFailure = new ProviderUnavailableException("no cart") };

        var result = await CartFiller.FillAsync(session, Items(2), CancellationToken.None);

        Assert.Equal(2, result.Added.Count);
        Assert.Null(result.Cart);
    }

    // ---- History in the fill (T-114) ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_history_is_read_once_after_the_last_search_and_before_the_first_add()
    {
        var session = new FakeSession
        {
            Hits = q => [Hit(q + "-a"), Hit(q + "-b")],
            Usual = new HashSet<string> { "item2-b", "item13-b" },
        };

        var result = await CartFiller.FillAsync(session, Items(25), CancellationToken.None);

        Assert.Equal(1, session.HistoryReads);
        Assert.Equal(["search", "search", "search", "history", "add", "add", "add"], session.Calls);
        Assert.Equal(UsuallyBoughtStatus.Used, result.UsuallyBought);
        Assert.Equal(2, result.UsuallyBoughtCount);
        Assert.Equal(["item2", "item13"], result.Added.Where(a => a.Source == ProductMatchSource.UsuallyBought).Select(a => a.Name).ToArray());
        Assert.Contains("item2-b", session.Adds.SelectMany(a => a));
        Assert.DoesNotContain("item2-a", session.Adds.SelectMany(a => a));
        Assert.Contains("item3-a", session.Adds.SelectMany(a => a));
    }

    [Fact]
    public async Task The_history_is_skipped_when_no_item_has_an_available_hit()
    {
        var session = new FakeSession { Hits = q => q == "item1" ? [] : [Hit("x", available: false)] };

        var result = await CartFiller.FillAsync(session, Items(3), CancellationToken.None);

        Assert.Equal(0, session.HistoryReads);
        Assert.Equal(UsuallyBoughtStatus.Skipped, result.UsuallyBought);
    }

    [Fact]
    public async Task A_history_failure_falls_back_to_first_hits_without_a_retry()
    {
        var session = new FakeSession
        {
            HistoryFailure = new ProviderUnavailableException("429"),
            Hits = q => [Hit(q + "-a"), Hit(q + "-b")],
            Usual = new HashSet<string> { "item1-b" },
        };

        var result = await CartFiller.FillAsync(session, Items(2), CancellationToken.None);

        Assert.Equal(1, session.HistoryReads);
        Assert.Equal(UsuallyBoughtStatus.Unavailable, result.UsuallyBought);
        Assert.Equal(0, result.UsuallyBoughtCount);
        Assert.Equal(2, result.Added.Count);
        Assert.All(result.Added, a => Assert.Equal(ProductMatchSource.FirstHit, a.Source));
    }

    [Fact]
    public async Task An_empty_history_is_used_and_matches_nothing()
    {
        var session = new FakeSession();

        var result = await CartFiller.FillAsync(session, Items(2), CancellationToken.None);

        Assert.Equal(UsuallyBoughtStatus.Used, result.UsuallyBought);
        Assert.Equal(0, result.UsuallyBoughtCount);
    }

    [Fact]
    public async Task Cancellation_while_reading_the_history_is_not_swallowed()
    {
        var session = new FakeSession { HistoryFailure = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => CartFiller.FillAsync(session, Items(2), CancellationToken.None));
    }

    private sealed class MismatchedSession : ICartProviderSession
    {
        public Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<ProductCandidate>>>([[Hit("1")]]);

        public Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<IReadOnlySet<string>> ReadUsuallyBoughtIdsAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
