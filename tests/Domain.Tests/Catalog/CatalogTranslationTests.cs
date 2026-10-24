using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Localization;
using Foodplanner.Domain.Recipes;

namespace Foodplanner.Domain.Tests.Catalog;

public class CatalogTranslationTests
{
    [Fact]
    public void Every_seeded_row_has_a_name_in_every_supported_locale()
    {
        var rows = CatalogTranslation.SeedRows().ToList();
        var expected = (IngredientSeed.All.Count + UnitSeed.All.Count + TagSeed.All.Count) * Locales.Supported.Count;

        Assert.Equal(107, IngredientSeed.All.Count);
        Assert.Equal(11, UnitSeed.All.Count);
        Assert.Equal(13, TagSeed.All.Count);
        Assert.Equal(expected, rows.Count);
        Assert.Equal(expected, rows.Select(r => (r.Kind, r.EntityId, r.Locale)).Distinct().Count()); // no duplicate key
        Assert.All(rows, r =>
        {
            Assert.Contains(r.Locale, Locales.Supported);
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.InRange(r.Name.Length, 1, CatalogTranslation.MaxNameLength);
        });
    }

    [Fact]
    public void Names_are_unique_per_kind_and_locale()
    {
        foreach (var kind in new[] { CatalogTranslation.KindIngredient, CatalogTranslation.KindTag })
        {
            var rows = CatalogTranslation.SeedRows().Where(r => r.Kind == kind).ToList();
            foreach (var locale in Locales.Supported)
            {
                var names = rows.Where(r => r.Locale == locale).Select(r => r.Name.ToUpperInvariant()).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
            }
        }
    }

    [Fact]
    public void The_english_name_is_the_one_stored_on_the_row()
    {
        var onion = IngredientSeed.All.Single(r => r.Key == "loek");
        Assert.Equal("Onion", onion.Names[Locales.English]);
        Assert.Equal("Løk", onion.Names[Locales.Norwegian]);
        Assert.Equal(onion.Name, onion.Names[Locales.English]);
        Assert.Equal("tablespoon", UnitSeed.All.Single(r => r.Code == "ss").Names[Locales.English]);
        Assert.Equal("spiseskje", UnitSeed.All.Single(r => r.Code == "ss").Names[Locales.Norwegian]);
    }

    [Fact]
    public void Seed_ids_did_not_change()
    {
        // Pinned in T-020; the translation work must not move any id.
        Assert.Equal(new Guid("e0ba0668-a3ec-50ba-9063-115da67c2e6a"), IngredientSeed.All.Single(r => r.Key == "salt").Id);
        Assert.Equal(new Guid("21341ca2-75fd-5c45-864a-084b06676bc3"), UnitSeed.All.Single(r => r.Code == "g").Id);
    }
}
