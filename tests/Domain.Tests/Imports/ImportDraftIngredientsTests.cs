using Foodplanner.Domain.Imports;

namespace Foodplanner.Domain.Tests.Imports;

public class ImportDraftIngredientsTests
{
    private static readonly Guid Old = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid New = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private static ImportDraftLine Line(Guid? ingredient, params Guid[] suggestions) =>
        new("2 dl x", "x", 2, null, "dl", null, Guid.NewGuid(), ingredient, 1, null, false,
            [.. suggestions.Select(s => new IngredientSuggestion(s, "n", 0.6))]);

    private static ImportDraft Draft(params ImportDraftLine[] lines) =>
        new("T", null, 4, null, null, null, [], lines, []);

    [Fact]
    public void Lines_and_suggestions_are_rewritten_and_the_rest_is_left_alone()
    {
        var draft = Draft(Line(Old), Line(Other, Old, Other), Line(null, Old));

        var result = draft.ReplaceIngredient(Old, New);

        Assert.Equal([New, Other, null], result.Lines.Select(l => l.IngredientId));
        Assert.Equal([Other], result.Lines[1].Suggestions.Select(s => s.IngredientId));
        Assert.Empty(result.Lines[2].Suggestions);
        Assert.Equal(draft.Lines[0].Quantity, result.Lines[0].Quantity);
        Assert.Equal(ImportLineStatus.Matched, result.Lines[0].Status);
        Assert.False(result.References(Old));
        Assert.True(draft.References(Old));
    }

    [Fact]
    public void Dropping_only_suggestions_refuses_while_a_line_uses_the_ingredient()
    {
        Assert.Throws<InvalidOperationException>(() => Draft(Line(Old)).ReplaceIngredient(Old, null));
        var cleaned = Draft(Line(Other, Old)).ReplaceIngredient(Old, null);
        Assert.Empty(cleaned.Lines[0].Suggestions);
        Assert.True(Draft(Line(Old)).UsesIngredient(Old));
        Assert.False(Draft(Line(Other, Old)).UsesIngredient(Old));
    }
}
