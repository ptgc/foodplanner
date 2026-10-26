using System.Text.Json.Serialization;
using Foodplanner.Domain.Common;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Imports;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportSource
{
    Url,
    Text,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportStatus
{
    /// <summary>Parsed and waiting for review; can be edited, saved or discarded.</summary>
    Draft,

    /// <summary>Turned into a recipe (<see cref="RecipeImport.SavedRecipeId"/>). The artifact is kept.</summary>
    Saved,

    /// <summary>Thrown away by the household; the raw content and the draft are erased.</summary>
    Discarded,
}

/// <summary>
/// The import artifact (T-042): where a recipe came from, the original text or JSON-LD, and the parsed, editable draft.
/// The original text exists only here (CLAUDE.md rule 5); a saved recipe has structured ingredients only. The draft is
/// JSON (<see cref="ImportDraftJson"/>) with no foreign keys: its ingredient and unit ids are re-checked through the
/// tenant-filtered sets whenever they are written to a recipe. HouseholdId is assigned by Infrastructure; the creation time
/// is the entity's <c>CreatedAt</c> audit column. <see cref="RowVersion"/> makes two concurrent saves settle to one recipe.
/// </summary>
public sealed class RecipeImport : Entity, ITenantEntity
{
    public const int MaxRawLength = 200_000;
    public const int MaxUrlLength = 2000;

    private RecipeImport()
    {
    }

    public RecipeImport(ImportSource source, string? sourceUrl, string rawContent, string draftJson, Guid createdByUserId)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (source == ImportSource.Url && string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ArgumentException("A URL import needs its URL.", nameof(sourceUrl));
        }

        if (sourceUrl is { Length: > MaxUrlLength })
        {
            throw new ArgumentException("The URL is too long.", nameof(sourceUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(draftJson);
        if (createdByUserId == Guid.Empty)
        {
            throw new ArgumentException("An import needs the user who made it.", nameof(createdByUserId));
        }

        Source = source;
        SourceUrl = sourceUrl;
        RawContent = Cap(rawContent);
        DraftJson = draftJson;
        CreatedByUserId = createdByUserId;
        Status = ImportStatus.Draft;
    }

    public Guid HouseholdId { get; private set; }

    public ImportSource Source { get; private set; }

    public string? SourceUrl { get; private set; }

    /// <summary>The pasted text, or the JSON of the page's Recipe node, cut to <see cref="MaxRawLength"/>. Null once discarded.</summary>
    public string? RawContent { get; private set; }

    /// <summary>The draft as JSON. Null once discarded.</summary>
    public string? DraftJson { get; private set; }

    public ImportStatus Status { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>The recipe made from this import. A plain id, not a foreign key: deleting the recipe leaves it dangling, harmlessly.</summary>
    public Guid? SavedRecipeId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public void UpdateDraft(string draftJson)
    {
        RequireDraft();
        ArgumentException.ThrowIfNullOrWhiteSpace(draftJson);
        DraftJson = draftJson;
    }

    public void MarkSaved(Guid recipeId)
    {
        RequireDraft();
        if (recipeId == Guid.Empty)
        {
            throw new ArgumentException("A saved import needs its recipe.", nameof(recipeId));
        }

        SavedRecipeId = recipeId;
        Status = ImportStatus.Saved;
    }

    /// <summary>Marks the import thrown away and erases the original text and the draft (nothing to keep).</summary>
    public void Discard()
    {
        RequireDraft();
        Status = ImportStatus.Discarded;
        RawContent = null;
        DraftJson = null;
    }

    private void RequireDraft()
    {
        if (Status != ImportStatus.Draft)
        {
            throw new InvalidOperationException($"The import is {Status}, not a draft.");
        }
    }

    private static string Cap(string raw) => raw.Length <= MaxRawLength ? raw : raw[..MaxRawLength];
}
