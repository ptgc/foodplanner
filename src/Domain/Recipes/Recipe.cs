using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Recipes;

/// <summary>
/// A household's recipe. Steps (<see cref="RecipeStep"/>) and ingredients (<see cref="RecipeIngredient"/>) are separate
/// tenant entities that point to it by id; there are no navigations (see docs/domain.md, T-021). HouseholdId is
/// assigned by Infrastructure when the recipe is added.
/// </summary>
public sealed class Recipe : Entity, ITenantEntity
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MinServings = 1;
    public const int MaxServings = 100;
    public const int MaxMinutes = 1440;
    public const int MaxSteps = 50;
    public const int MaxIngredients = 100;
    public const int MaxSourceUrlLength = 2000;
    public const int MaxSourceNameLength = 100;

    private Recipe()
    {
    }

    public Recipe(string title, string? description, int servings, int? prepMinutes, int? cookMinutes) =>
        Set(title, description, servings, prepMinutes, cookMinutes);

    public Guid HouseholdId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int Servings { get; private set; }

    public int? PrepMinutes { get; private set; }

    public int? CookMinutes { get; private set; }

    /// <summary>Prep plus cook time; null when neither is known.</summary>
    public int? TotalMinutes => PrepMinutes is null && CookMinutes is null ? null : (PrepMinutes ?? 0) + (CookMinutes ?? 0);

    /// <summary>
    /// The version of the current image, or null when the recipe has none. Every upload gets a new version, so the
    /// stored files and their URLs never change once written and caches can keep them forever.
    /// </summary>
    public Guid? ImageVersion { get; private set; }

    public void SetImage(Guid version)
    {
        if (version == Guid.Empty)
        {
            throw new ArgumentException("An image version is required.", nameof(version));
        }

        ImageVersion = version;
    }

    public void ClearImage() => ImageVersion = null;

    /// <summary>Where the recipe came from (an http or https page), or null. Shown as a link to the original (T-075).</summary>
    public string? SourceUrl { get; private set; }

    /// <summary>A short display name for the source, e.g. "matprat.no". Null means "derive it from the host" (<see cref="SourceDisplayName"/>).</summary>
    public string? SourceName { get; private set; }

    /// <summary>The name to show: the stored one, else the URL's host without "www.", else null.</summary>
    public string? SourceDisplayName => SourceUrl is null ? null : SourceName ?? DeriveSourceName(SourceUrl);

    /// <summary>True for an absolute http or https URL of at most <see cref="MaxSourceUrlLength"/> characters.</summary>
    public static bool IsValidSourceUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.Length <= MaxSourceUrlLength && !url.Any(char.IsControl)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0;

    /// <summary>The host without a leading "www.", or null when the URL is not a valid source URL.</summary>
    public static string? DeriveSourceName(string url)
    {
        if (!IsValidSourceUrl(url))
        {
            return null;
        }

        var host = new Uri(url).Host;
        host = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return host.Length is > 0 and <= MaxSourceNameLength ? host : null;
    }

    /// <summary>Sets or clears (null url) the source. A blank name is derived from the host.</summary>
    public void SetSource(string? url, string? name)
    {
        if (url is null)
        {
            SourceUrl = null;
            SourceName = null;
            return;
        }

        if (!IsValidSourceUrl(url))
        {
            throw new ArgumentException("The source must be an absolute http or https URL.", nameof(url));
        }

        if (name is { Length: > MaxSourceNameLength })
        {
            throw new ArgumentException("The source name is too long.", nameof(name));
        }

        SourceUrl = url;
        SourceName = string.IsNullOrWhiteSpace(name) ? DeriveSourceName(url) : name;
    }

    public void Update(string title, string? description, int servings, int? prepMinutes, int? cookMinutes) =>
        Set(title, description, servings, prepMinutes, cookMinutes);

    private void Set(string title, string? description, int servings, int? prepMinutes, int? cookMinutes)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            throw new ArgumentException("Invalid recipe title.", nameof(title));
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            throw new ArgumentException("The description is too long.", nameof(description));
        }

        if (servings is < MinServings or > MaxServings)
        {
            throw new ArgumentOutOfRangeException(nameof(servings));
        }

        if (prepMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(prepMinutes));
        }

        if (cookMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(cookMinutes));
        }

        Title = title;
        Description = description;
        Servings = servings;
        PrepMinutes = prepMinutes;
        CookMinutes = cookMinutes;
    }
}
