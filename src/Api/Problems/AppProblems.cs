namespace Foodplanner.Api.Problems;

/// <summary>
/// Application errors are RFC 9457 ProblemDetails with <c>type</c> = <c>urn:foodplanner:problem:&lt;code&gt;</c>.
/// Validation errors use the standard <c>Results.ValidationProblem</c> shape instead.
/// </summary>
public static class AppProblems
{
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string LoginFailed = "login-failed";
    public const string ConfirmationInvalid = "confirmation-invalid";
    public const string AntiforgeryInvalid = "antiforgery-invalid";
    public const string RateLimited = "rate-limited";
    public const string InviteInvalid = "invite-invalid";
    public const string AlreadyMember = "already-member";
    public const string IngredientExists = "ingredient-exists";
    public const string TagExists = "tag-exists";
    public const string RecipeNotFound = "recipe-not-found";
    public const string RecipeConflict = "recipe-conflict";
    public const string RecipeInUse = "recipe-in-use";
    public const string MealPlanEntryNotFound = "meal-plan-entry-not-found";
    public const string MealPlanEntryConflict = "meal-plan-entry-conflict";
    public const string ShoppingListItemNotFound = "shopping-list-item-not-found";
    public const string ShoppingListItemGenerated = "shopping-list-item-generated";
    public const string ShoppingListConflict = "shopping-list-conflict";

    public static string Type(string code) => $"urn:foodplanner:problem:{code}";

    public static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, type: Type(code), title: title, detail: detail);

    /// <summary>For code that runs outside an endpoint (cookie events, rate limiter rejection).</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string title, string? detail = null) =>
        Problem(status, code, title, detail).ExecuteAsync(context);
}
