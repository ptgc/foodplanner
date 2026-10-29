using Foodplanner.Api.Localization;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.ShoppingLists.Export;
using Foodplanner.Infrastructure.Export;
using Foodplanner.Infrastructure.Persistence;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// The export of a list. <c>Title</c> and <c>Text</c> are meant for <c>navigator.share</c> (title plus text) and for
/// copy; <c>Url</c> is for exporters that open something elsewhere (none yet).
/// </summary>
public sealed record ShoppingListExportResponse(string Format, string Title, string? Text, string? Url);

/// <summary>Share/copy export (T-050): <c>GET .../shopping-lists/{weekStart}/export?format=text</c>, any member. See docs/domain.md.</summary>
public static class ShoppingListExportEndpoints
{
    public static IEndpointRouteBuilder MapShoppingListExportEndpoints(this IEndpointRouteBuilder api)
    {
        var lists = api.MapHouseholdScopedGroup().MapGroup("/shopping-lists/{weekStart}");

        lists.MapGet("/export", Export)
            .WithName("ExportShoppingList")
            .Produces<ShoppingListExportResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<IResult> Export(
        string weekStart, string? format, RequestLocale locale, AppDbContext db, IShoppingListExporterSelector exporters,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ShoppingListEndpoints.IsWeekStart(weekStart, out var monday))
        {
            errors["weekStart"] = ["The week must be the Monday of an ISO week, as yyyy-MM-dd."];
        }

        var exporter = exporters.Find(string.IsNullOrWhiteSpace(format) ? TextShoppingListExporter.ExporterName : format);
        if (exporter is null)
        {
            errors["format"] = ["Unknown export format."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var list = await ShoppingListEndpoints.ReadAsync(db, monday, locale.Code, cancellationToken);
        var result = await exporter!.ExportAsync(
            new ShoppingListExport(monday, locale.Code, [.. list.Main.Select(i => ToExport(i, withQuantities: true))], [.. list.CheckYouHave.Select(i => ToExport(i, withQuantities: false))]),
            cancellationToken);
        return Results.Ok(new ShoppingListExportResponse(exporter.Name, result.Title, result.Text, result.Url?.ToString()));
    }

    /// <summary>"Check you have" items carry the quantity the plan needs since T-093 (a covered item); the export keeps them as names only, as before.</summary>
    private static ShoppingListExportItem ToExport(ShoppingListItemResponse item, bool withQuantities) =>
        new(item.Name, item.IsManual, item.Checked,
            [.. withQuantities ? item.Quantities.Select(q => new ShoppingListExportQuantity(q.Amount, q.UnitCode, q.UnitName)) : []]);
}
