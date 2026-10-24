using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Localization;
using Microsoft.EntityFrameworkCore;

namespace Foodplanner.Infrastructure.Persistence.Localization;

/// <summary>A catalog row with its display name for one locale.</summary>
public sealed class Named<T>
    where T : class, ICatalogNamed
{
    public required T Row { get; init; }

    public required string Name { get; init; }
}

/// <summary>
/// Localized catalog names as part of the query (T-039), so search, sort and paging happen in SQL on the localized name.
/// The name of a global row is its translation in the requested locale, else its English translation, else the row's own
/// <c>Name</c> (the English key). A household's own row has no translation and keeps the name it was typed with.
/// </summary>
public static class CatalogNames
{
    // Windows collations, so ordering does not depend on ICU in the runtime image (the API container may run with
    // invariant globalization). Norwegian_100 puts æ, ø, å after z in that order; the Latin1 one is the English ordering.
    private const string NorwegianCollation = "Norwegian_100_CI_AS";
    private const string EnglishCollation = "Latin1_General_100_CI_AS";

    public static IQueryable<Named<T>> WithNames<T>(this IQueryable<T> source, AppDbContext db, string kind, string locale)
        where T : class, ICatalogNamed
    {
        var requested = db.CatalogTranslations.Where(t => t.Kind == kind && t.Locale == locale);
        var english = db.CatalogTranslations.Where(t => t.Kind == kind && t.Locale == Locales.English);
        return from row in source
               join r in requested on row.Id equals r.EntityId into requestedRows
               from r in requestedRows.DefaultIfEmpty()
               join e in english on row.Id equals e.EntityId into englishRows
               from e in englishRows.DefaultIfEmpty()
               select new Named<T> { Row = row, Name = r.Name ?? e.Name ?? row.Name };
    }

    /// <summary>Orders by the localized name using the locale's collation, then by id so paging is stable.</summary>
    public static IOrderedQueryable<Named<T>> OrderByName<T>(this IQueryable<Named<T>> source, string locale)
        where T : class, ICatalogNamed =>
        (locale == Locales.Norwegian
            ? source.OrderBy(n => EF.Functions.Collate(n.Name, NorwegianCollation))
            : source.OrderBy(n => EF.Functions.Collate(n.Name, EnglishCollation)))
        .ThenBy(n => n.Row.Id);

    /// <summary>As <see cref="OrderByName{T}"/>, as a secondary ordering.</summary>
    public static IOrderedQueryable<Named<T>> ThenByName<T>(this IOrderedQueryable<Named<T>> source, string locale)
        where T : class, ICatalogNamed =>
        (locale == Locales.Norwegian
            ? source.ThenBy(n => EF.Functions.Collate(n.Name, NorwegianCollation))
            : source.ThenBy(n => EF.Functions.Collate(n.Name, EnglishCollation)))
        .ThenBy(n => n.Row.Id);

    /// <summary>The localized names of the given rows by id (used when a response only carries ids).</summary>
    public static async Task<Dictionary<Guid, string>> NamesByIdAsync<T>(
        this IQueryable<T> source, AppDbContext db, string kind, string locale, CancellationToken cancellationToken)
        where T : class, ICatalogNamed =>
        await source.WithNames(db, kind, locale).ToDictionaryAsync(n => n.Row.Id, n => n.Name, cancellationToken);

    /// <summary>True when a global row of the kind has this name in any locale (case-insensitive by the database collation).</summary>
    public static Task<bool> AnyGlobalNameAsync(this AppDbContext db, string kind, string name, CancellationToken cancellationToken) =>
        db.CatalogTranslations.AnyAsync(t => t.Kind == kind && t.Name == name, cancellationToken);
}
