using Foodplanner.Domain.ShoppingLists;
using Foodplanner.Domain.ShoppingLists.Cart;
using Foodplanner.Domain.ShoppingLists.Sync;

namespace Foodplanner.Domain.Tests.ShoppingLists;

/// <summary>T-130: which ingredients a sync checks, the classification of the review rows, the call plan and the bulk save rules (pure Domain).</summary>
public class OdaSyncTests
{
    private static ProductCandidate Hit(string id, bool available = true) => new(id, $"Product {id}", available);

    private static IReadOnlySet<string> Usual(params string[] ids) => new HashSet<string>(ids, StringComparer.Ordinal);

    private static SyncIngredient Ing(string name, int recipes = 1, bool staple = false, bool choice = false, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, recipes, staple, choice);

    // ---- Selection ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Selection_orders_by_recipe_count_then_name_then_id()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var all = new[] { Ing("Egg", 1), Ing("Rice", 3), Ing("Apple", 1), Ing("Same", 1, id: high), Ing("Same", 1, id: low) };

        var selection = SyncSelector.Select(all, false, false, 0, 100, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["Rice", "Apple", "Egg", "Same", "Same"], selection.Selected.Select(i => i.Name).ToArray());
        Assert.Equal([low, high], selection.Selected.Where(i => i.Name == "Same").Select(i => i.IngredientId).ToArray());
        Assert.Equal(0, selection.NotCheckedCount);
    }

    [Fact]
    public void Staples_and_existing_choices_are_left_out_unless_asked_for()
    {
        var all = new[] { Ing("Salt", 5, staple: true), Ing("Rice", 2, choice: true), Ing("Egg", 1), Ing("Unused", 0) };

        Assert.Equal(["Egg"], Names(SyncSelector.Select(all, false, false, 0, 10, StringComparer.Ordinal)));
        Assert.Equal(["Salt", "Egg"], Names(SyncSelector.Select(all, true, false, 0, 10, StringComparer.Ordinal)));
        Assert.Equal(["Rice", "Egg"], Names(SyncSelector.Select(all, false, true, 0, 10, StringComparer.Ordinal)));
        Assert.Equal(["Salt", "Rice", "Egg"], Names(SyncSelector.Select(all, true, true, 0, 10, StringComparer.Ordinal)));
    }

    [Fact]
    public void Offset_and_cap_page_the_same_list_without_overlap_or_gaps()
    {
        var all = Enumerable.Range(0, 25).Select(i => Ing($"Item {i:00}", 100 - i)).ToArray();

        var first = SyncSelector.Select(all, false, false, 0, 10, StringComparer.Ordinal);
        var second = SyncSelector.Select(all, false, false, 10, 10, StringComparer.Ordinal);
        var third = SyncSelector.Select(all, false, false, 20, 10, StringComparer.Ordinal);
        var beyond = SyncSelector.Select(all, false, false, 40, 10, StringComparer.Ordinal);

        Assert.Equal(15, first.NotCheckedCount);
        Assert.Equal(5, second.NotCheckedCount);
        Assert.Equal(0, third.NotCheckedCount);
        Assert.Equal(all.Select(i => i.Name), first.Selected.Concat(second.Selected).Concat(third.Selected).Select(i => i.Name));
        Assert.Empty(beyond.Selected);
        Assert.Equal(0, beyond.NotCheckedCount);
    }

    [Fact]
    public void Selection_rejects_a_negative_offset_and_a_cap_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncSelector.Select([], false, false, -1, 10, StringComparer.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncSelector.Select([], false, false, 0, 0, StringComparer.Ordinal));
        Assert.Throws<ArgumentNullException>(() => SyncSelector.Select(null!, false, false, 0, 10, StringComparer.Ordinal));
    }

    private static string[] Names(SyncSelection selection) => [.. selection.Selected.Select(i => i.Name)];

    // ---- Classification ----------------------------------------------------------------------------------------------

    private static SyncRow One(IReadOnlyList<ProductCandidate> hits, IReadOnlySet<string>? usual = null, string? existing = null) =>
        SyncClassifier.ClassifyOne(new SyncSearched(Guid.NewGuid(), "Rice", hits, existing), usual ?? Usual());

    [Fact]
    public void A_usual_hit_is_usually_bought_and_pre_ticked_and_the_best_ranked_usual_hit_wins()
    {
        var row = One([Hit("1"), Hit("2"), Hit("3")], Usual("3", "2"));

        Assert.Equal(SyncCategory.UsuallyBought, row.Category);
        Assert.Equal("2", row.SelectedProductId);
        Assert.True(row.Preticked);
        Assert.False(row.HasExistingChoice);
        Assert.Equal(["1", "2", "3"], row.Options.Select(o => o.ProductId).ToArray());
        Assert.Equal(["2", "3"], row.Options.Where(o => o.IsUsual).Select(o => o.ProductId).ToArray());
    }

    [Fact]
    public void Without_a_usual_hit_the_first_available_hit_is_an_unticked_suggestion()
    {
        var row = One([Hit("1", available: false), Hit("2"), Hit("3")], Usual("99"));

        Assert.Equal(SyncCategory.Suggestion, row.Category);
        Assert.Equal("2", row.SelectedProductId);
        Assert.False(row.Preticked);
        Assert.Equal(["2", "3"], row.Options.Select(o => o.ProductId).ToArray());
    }

    [Fact]
    public void A_usual_id_that_the_search_did_not_return_is_never_offered_and_an_unavailable_usual_hit_is_not_used()
    {
        var notReturned = One([Hit("1"), Hit("2")], Usual("99"));
        Assert.Equal("1", notReturned.SelectedProductId);
        Assert.DoesNotContain(notReturned.Options, o => o.ProductId == "99");

        var soldOut = One([Hit("1"), Hit("2", available: false)], Usual("2"));
        Assert.Equal(SyncCategory.Suggestion, soldOut.Category);
        Assert.Equal("1", soldOut.SelectedProductId);
    }

    [Fact]
    public void No_hits_and_only_unavailable_hits_are_not_found_with_their_reason_and_no_choice_to_make()
    {
        var none = One([]);
        Assert.Equal((SyncCategory.NotFound, SyncNotFoundReason.NoMatch), (none.Category, none.Reason));
        Assert.Null(none.SelectedProductId);
        Assert.False(none.Preticked);
        Assert.Empty(none.Options);

        var soldOut = One([Hit("1", false), Hit("2", false)], Usual("1"));
        Assert.Equal((SyncCategory.NotFound, SyncNotFoundReason.Unavailable), (soldOut.Category, soldOut.Reason));
    }

    [Fact]
    public void A_row_has_at_most_five_available_options_in_rank_order_with_the_selected_product_among_them()
    {
        var hits = Enumerable.Range(1, 9).Select(i => Hit(i.ToString(), available: i != 2)).ToList();
        var row = One(hits, Usual("8"));

        Assert.Equal("8", row.SelectedProductId);
        Assert.Equal(5, row.Options.Count);
        Assert.Contains(row.Options, o => o.ProductId == "8");
        Assert.DoesNotContain(row.Options, o => o.ProductId == "2");
        Assert.Equal(["1", "3", "4", "5", "8"], row.Options.Select(o => o.ProductId).ToArray());
    }

    [Fact]
    public void An_existing_choice_among_the_candidates_is_current_unticked_and_changes_nothing()
    {
        var row = One([Hit("1"), Hit("2"), Hit("3")], Usual("3"), existing: "2");

        Assert.Equal(SyncCategory.Current, row.Category);
        Assert.Null(row.SelectedProductId);
        Assert.False(row.Preticked);
        Assert.True(row.HasExistingChoice);
        Assert.Equal(["2"], row.Options.Where(o => o.IsCurrent).Select(o => o.ProductId).ToArray());
    }

    [Fact]
    public void An_unusable_existing_choice_gives_an_unticked_replaces_row_even_when_a_usual_hit_exists()
    {
        var gone = One([Hit("1"), Hit("3")], Usual("3"), existing: "2");
        Assert.Equal(SyncCategory.UsuallyBought, gone.Category);
        Assert.Equal("3", gone.SelectedProductId);
        Assert.True(gone.HasExistingChoice);
        Assert.False(gone.Preticked);

        var soldOut = One([Hit("1"), Hit("2", available: false)], existing: "2");
        Assert.Equal(SyncCategory.Suggestion, soldOut.Category);
        Assert.True(soldOut.HasExistingChoice);
        Assert.False(soldOut.Preticked);

        var nothing = One([Hit("2", available: false)], existing: "2");
        Assert.Equal(SyncCategory.NotFound, nothing.Category);
        Assert.True(nothing.HasExistingChoice);
    }

    [Fact]
    public void Rows_are_ordered_by_category_then_run_order_and_an_empty_usual_set_and_null_arguments_behave()
    {
        var a = new SyncSearched(Guid.NewGuid(), "A", [], null);
        var b = new SyncSearched(Guid.NewGuid(), "B", [Hit("1")], null);
        var c = new SyncSearched(Guid.NewGuid(), "C", [Hit("2")], null);
        var d = new SyncSearched(Guid.NewGuid(), "D", [Hit("3")], null);

        var rows = SyncClassifier.Classify([a, b, c, d], Usual("3"));

        Assert.Equal(["D", "B", "C", "A"], rows.Select(r => r.IngredientName).ToArray());
        Assert.Empty(SyncClassifier.Classify([], Usual()));
        Assert.Throws<ArgumentNullException>(() => SyncClassifier.Classify(null!, Usual()));
        Assert.Throws<ArgumentNullException>(() => SyncClassifier.Classify([], null!));
        Assert.Throws<ArgumentNullException>(() => SyncClassifier.ClassifyOne(null!, Usual()));
    }

    // ---- The run -----------------------------------------------------------------------------------------------------

    private sealed class FakeSession : ICartProviderSession
    {
        public List<string[]> Searches { get; } = [];

        public int HistoryCalls { get; private set; }

        public Func<int, Exception?> SearchFailure { get; set; } = _ => null;

        public Exception? HistoryFailure { get; set; }

        public Action? OnSearch { get; set; }

        public Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken)
        {
            Searches.Add([.. queries]);
            OnSearch?.Invoke();
            if (SearchFailure(Searches.Count) is { } failure)
            {
                throw failure;
            }

            return Task.FromResult<IReadOnlyList<IReadOnlyList<ProductCandidate>>>([.. queries.Select(q => (IReadOnlyList<ProductCandidate>)[Hit("id-" + q)])]);
        }

        public Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A sync never adds to the cart.");

        public Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A sync never reads the cart.");

        public Task<IReadOnlySet<string>> ReadUsuallyBoughtIdsAsync(CancellationToken cancellationToken)
        {
            HistoryCalls++;
            return HistoryFailure is { } failure ? throw failure : Task.FromResult<IReadOnlySet<string>>(Usual("id-Item 03"));
        }
    }

    private static SyncIngredient[] Many(int count) => [.. Enumerable.Range(0, count).Select(i => Ing($"Item {i:00}"))];

    private static readonly IReadOnlyDictionary<Guid, string> NoChoices = new Dictionary<Guid, string>();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    [InlineData(150, 15)]
    [InlineData(200, 20)]
    public async Task Searches_are_batches_of_ten_one_after_the_other_and_one_history_call_follows(int ingredients, int searches)
    {
        var session = new FakeSession();

        var result = await SyncRunner.RunAsync(session, Many(ingredients), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);

        Assert.Equal(searches, session.Searches.Count);
        Assert.All(session.Searches, s => Assert.InRange(s.Length, 1, 10));
        Assert.Equal(1, session.HistoryCalls);
        Assert.Null(result.Stopped);
        Assert.Equal(UsuallyBoughtStatus.Used, result.UsuallyBought);
        Assert.Equal(ingredients, result.SearchedCount);
        Assert.Equal(ingredients, result.Rows.Count);
    }

    [Fact]
    public async Task No_available_hit_means_no_history_call()
    {
        var session = new FakeSession();
        var empty = new EmptySession(session);

        var result = await SyncRunner.RunAsync(empty, Many(3), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);

        Assert.Equal(0, session.HistoryCalls);
        Assert.Equal(UsuallyBoughtStatus.Skipped, result.UsuallyBought);
        Assert.All(result.Rows, r => Assert.Equal(SyncCategory.NotFound, r.Category));
    }

    private sealed class EmptySession(FakeSession inner) : ICartProviderSession
    {
        public Task<IReadOnlyList<IReadOnlyList<ProductCandidate>>> SearchAsync(IReadOnlyList<string> queries, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<ProductCandidate>>>([.. queries.Select(_ => (IReadOnlyList<ProductCandidate>)[])]);

        public Task<IReadOnlySet<string>> AddOnePackEachAsync(IReadOnlyList<string> productIds, CancellationToken cancellationToken) => inner.AddOnePackEachAsync(productIds, cancellationToken);

        public Task<CartSnapshot> ReadCartAsync(CancellationToken cancellationToken) => inner.ReadCartAsync(cancellationToken);

        public Task<IReadOnlySet<string>> ReadUsuallyBoughtIdsAsync(CancellationToken cancellationToken) => inner.ReadUsuallyBoughtIdsAsync(cancellationToken);
    }

    [Fact]
    public async Task A_usage_limit_on_the_third_search_returns_the_first_two_batches_without_retry_or_history()
    {
        var session = new FakeSession { SearchFailure = n => n == 3 ? new ProviderLimitException("limit") : null };

        var result = await SyncRunner.RunAsync(session, Many(45), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);

        Assert.Equal(SyncStopped.ProviderLimit, result.Stopped);
        Assert.Equal(20, result.SearchedCount);
        Assert.Equal(3, session.Searches.Count); // the failing call is the third and is not repeated
        Assert.Equal(0, session.HistoryCalls);
        Assert.Equal(20, result.Rows.Count);
    }

    [Fact]
    public async Task Another_failure_after_the_first_batch_stops_with_oda_error_but_a_failure_in_the_first_batch_throws()
    {
        var late = new FakeSession { SearchFailure = n => n == 2 ? new ProviderUnavailableException("boom") : null };
        var result = await SyncRunner.RunAsync(late, Many(25), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);
        Assert.Equal(SyncStopped.ProviderError, result.Stopped);
        Assert.Equal(10, result.SearchedCount);

        var first = new FakeSession { SearchFailure = _ => new ProviderLimitException("limit") };
        await Assert.ThrowsAsync<ProviderLimitException>(() =>
            SyncRunner.RunAsync(first, Many(25), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None));
        Assert.Single(first.Searches);
    }

    [Fact]
    public async Task The_time_budget_is_checked_before_every_call()
    {
        var clock = new Clock();
        var session = new FakeSession { OnSearch = () => clock.Advance(TimeSpan.FromSeconds(40)) };

        var result = await SyncRunner.RunAsync(session, Many(50), NoChoices, TimeSpan.FromSeconds(90), clock, CancellationToken.None);

        Assert.Equal(SyncStopped.Time, result.Stopped);
        Assert.Equal(3, session.Searches.Count); // 0 s, 40 s and 80 s are inside the budget; 120 s is not
        Assert.Equal(30, result.SearchedCount);
        Assert.Equal(0, session.HistoryCalls);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public async Task A_history_failure_falls_back_to_suggestions_and_is_not_retried()
    {
        var session = new FakeSession { HistoryFailure = new ProviderLimitException("limit") };

        var result = await SyncRunner.RunAsync(session, Many(5), NoChoices, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);

        Assert.Equal(UsuallyBoughtStatus.Unavailable, result.UsuallyBought);
        Assert.Equal(1, session.HistoryCalls);
        Assert.All(result.Rows, r => Assert.Equal(SyncCategory.Suggestion, r.Category));
    }

    [Fact]
    public async Task Existing_choices_are_passed_to_the_classification()
    {
        var ingredients = Many(2);
        var session = new FakeSession();
        var existing = new Dictionary<Guid, string> { [ingredients[0].IngredientId] = "id-Item 00" };

        var result = await SyncRunner.RunAsync(session, ingredients, existing, TimeSpan.FromSeconds(90), TimeProvider.System, CancellationToken.None);

        Assert.Contains(result.Rows, r => r.Category == SyncCategory.Current && r.IngredientId == ingredients[0].IngredientId);
    }

    // ---- Bulk save ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Bulk_plan_creates_replaces_only_when_ticked_skips_otherwise_and_leaves_the_same_id_alone()
    {
        var created = Guid.NewGuid();
        var replaced = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        var same = Guid.NewGuid();
        var sameWithReplace = Guid.NewGuid();
        var existing = new Dictionary<Guid, long> { [replaced] = 1, [skipped] = 2, [same] = 3, [sameWithReplace] = 4, [Guid.NewGuid()] = 5 };

        var plan = BulkPreferencePlanner.Plan(
            existing,
            [
                new BulkPreferenceItem(created, 10, false),
                new BulkPreferenceItem(replaced, 11, true),
                new BulkPreferenceItem(skipped, 12, false),
                new BulkPreferenceItem(same, 3, false),
                new BulkPreferenceItem(sameWithReplace, 4, true),
            ]);

        Assert.Equal([created], plan.Created.Select(i => i.IngredientId).ToArray());
        Assert.Equal([replaced], plan.Replaced.Select(i => i.IngredientId).ToArray());
        Assert.Equal([skipped], plan.SkippedExisting.ToArray());
        Assert.Equal([same, sameWithReplace], plan.Unchanged.ToArray());
        Assert.Equal(6, plan.RowCountAfter);
        Assert.False(plan.ExceedsLimit);
    }

    [Fact]
    public void Bulk_plan_counts_the_rows_after_the_call_against_the_household_limit()
    {
        var existing = Enumerable.Range(0, PreferredOdaProduct.MaxPerHousehold).ToDictionary(_ => Guid.NewGuid(), _ => 1L);
        var replaceOne = BulkPreferencePlanner.Plan(existing, [new BulkPreferenceItem(existing.Keys.First(), 99, true)]);
        var addOne = BulkPreferencePlanner.Plan(existing, [new BulkPreferenceItem(Guid.NewGuid(), 99, false)]);

        Assert.False(replaceOne.ExceedsLimit);
        Assert.True(addOne.ExceedsLimit);
        Assert.Equal(PreferredOdaProduct.MaxPerHousehold + 1, addOne.RowCountAfter);
    }
}
