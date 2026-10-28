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
    public const string ImageTooLarge = "image-too-large";
    public const string RecipeInUse = "recipe-in-use";
    public const string MealPlanEntryNotFound = "meal-plan-entry-not-found";
    public const string MealPlanEntryConflict = "meal-plan-entry-conflict";
    public const string ShoppingListItemNotFound = "shopping-list-item-not-found";
    public const string ShoppingListItemGenerated = "shopping-list-item-generated";
    public const string ShoppingListConflict = "shopping-list-conflict";
    public const string PantryConflict = "pantry-conflict";
    public const string ForbiddenOrigin = "forbidden-origin";
    public const string ImportNotFound = "import-not-found";
    public const string ImportNotDraft = "import-not-draft";
    public const string ImportConflict = "import-conflict";
    public const string ImportLimit = "import-limit";
    public const string ImportUrlNotAllowed = "import-url-not-allowed";
    public const string ImportFetchFailed = "import-fetch-failed";
    public const string ImportFetchTimeout = "import-fetch-timeout";
    public const string ImportBusy = "import-busy";
    public const string ImportTooLarge = "import-too-large";
    public const string ImportUnsupportedContent = "import-unsupported-content";
    public const string ImportNoRecipeFound = "import-no-recipe-found";

    public const string IngredientNotFound = "ingredient-not-found";
    public const string IngredientInUse = "ingredient-in-use";
    public const string IngredientConflict = "ingredient-conflict";
    public const string IngredientGlobal = "ingredient-global";
    public const string IngredientMergeIntoSelf = "ingredient-merge-into-self";

    public static string Type(string code) => $"urn:foodplanner:problem:{code}";

    /// <summary>A problem with extra members (RFC 9457 allows extensions), e.g. the usage counts of an ingredient in use.</summary>
    public static IResult Problem(int status, string code, string title, string? detail, IDictionary<string, object?> extensions) =>
        Results.Problem(statusCode: status, type: Type(code), title: title, detail: detail, extensions: extensions);

    public static IResult Problem(int status, string code, string title, string? detail = null) =>
        Results.Problem(statusCode: status, type: Type(code), title: title, detail: detail);

    /// <summary>For code that runs outside an endpoint (cookie events, rate limiter rejection).</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, string title, string? detail = null) =>
        Problem(status, code, title, detail).ExecuteAsync(context);
}
