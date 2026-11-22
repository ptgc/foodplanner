using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foodplanner.Domain.Catalog;
using Foodplanner.Domain.Recipes;
using Foodplanner.Infrastructure.Import.Ai;

namespace Foodplanner.Architecture.Tests;

/// <summary>Recipe from a photo (T-118): configuration defaults, the skill file and schema, and where the SDK and the eval script may be.</summary>
public partial class PhotoImportRulesTests
{
    [GeneratedRegex(@"unit codes given in the message: (?<codes>[a-z, ]+)\.")]
    private static partial Regex UnitLine();

    private static string Root => SourceScanner.SolutionRoot();

    // ---- Configuration ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_photo_import_defaults_to_off_with_the_spec_limits()
    {
        var options = new AiPhotoImportOptions();

        Assert.False(options.Enabled);
        Assert.Equal("claude-opus-5-5", options.Model);
        Assert.Equal("medium", options.Effort);
        Assert.Equal(12_000, options.MaxOutputTokens);
        Assert.Equal(90, options.TimeoutSeconds);
        Assert.Equal(4, options.MaxPhotos);
        Assert.Equal(2, options.DailyPerHousehold);
        Assert.Equal(300, options.GlobalMonthly);
        Assert.Equal(3, options.MaxConcurrent);
        // On needs the flag AND the shared key.
        Assert.False(new AiPhotoImportOptions { Enabled = true }.IsActive(new AiImportOptions()));
        Assert.False(new AiPhotoImportOptions().IsActive(new AiImportOptions { ApiKey = "k" }));
        Assert.True(new AiPhotoImportOptions { Enabled = true }.IsActive(new AiImportOptions { ApiKey = "k" }));
    }

    [Fact]
    public void Committed_settings_files_switch_the_photo_import_off_and_hold_no_key()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "src", "Api"), "appsettings*.json").ToList();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("AiPhotoImport", out var section))
            {
                continue;
            }

            Assert.False(section.TryGetProperty("ApiKey", out _));
            if (section.TryGetProperty("Enabled", out var enabled))
            {
                Assert.Equal(JsonValueKind.False, enabled.ValueKind);
            }
        }

        using var main = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Api", "appsettings.json")));
        Assert.Equal(JsonValueKind.False, main.RootElement.GetProperty("AiPhotoImport").GetProperty("Enabled").ValueKind);
    }

    // ---- The skill file and the schema -----------------------------------------------------------------------------------

    [Fact]
    public void The_skill_and_the_schema_are_embedded_and_the_first_line_is_the_version()
    {
        Assert.NotEmpty(RecipePhotoSkill.Text);
        Assert.Matches(@"^Version: \d+\n", RecipePhotoSkill.Text);
        Assert.True(RecipePhotoSkill.Version >= 1);
        Assert.NotEqual(JsonValueKind.Undefined, RecipePhotoSkill.Schema.ValueKind);
    }

    [Fact]
    public void The_skill_lists_exactly_the_unit_codes_of_the_app()
    {
        var match = UnitLine().Match(RecipePhotoSkill.Text);
        Assert.True(match.Success, "the skill must say which unit codes exist");
        var listed = match.Groups["codes"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(UnitSeed.All.Select(u => u.Code).Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_skill_lists_exactly_the_amount_kinds_and_purposes_and_the_schema_enums_equal_the_domain_enums()
    {
        var kinds = Enum.GetNames<AmountKind>().Order(StringComparer.Ordinal).ToArray();
        var purposes = Enum.GetNames<IngredientPurpose>().Order(StringComparer.Ordinal).ToArray();

        var enumLine = RecipePhotoSkill.Text.Split('\n').Single(l => l.StartsWith("The enums are exactly", StringComparison.Ordinal));
        var quoted = Regex.Matches(enumLine, "`(?<name>[A-Za-z]+)`").Select(m => m.Groups["name"].Value).ToArray();
        Assert.Equal([.. kinds, .. purposes], quoted.Order(StringComparer.Ordinal).Where(kinds.Contains).Concat(quoted.Order(StringComparer.Ordinal).Where(purposes.Contains)));
        Assert.Equal(kinds.Length + purposes.Length, quoted.Length);

        var properties = RecipePhotoSkill.Schema.GetProperty("properties").GetProperty("ingredients").GetProperty("items").GetProperty("properties");
        Assert.Equal(kinds, EnumOf(properties, "amountKind"));
        Assert.Equal(purposes, EnumOf(properties, "purpose"));
    }

    private static string[] EnumOf(JsonElement properties, string name) =>
        [.. properties.GetProperty(name).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal)];

    [Fact]
    public void The_skill_has_every_rule_heading_the_mushroom_rule_and_stays_under_6000_words()
    {
        foreach (var heading in new[] { "Task and safety", "Layout reading", "Text fields", "Ingredient groups", "Amounts", "Units", "Ingredient names", "Dish picture", "Output" })
        {
            Assert.Contains($"## {heading}", RecipePhotoSkill.Text, StringComparison.Ordinal);
        }

        // Architect 2026-11-22: mushroom kinds are different ingredients and are never merged.
        Assert.Contains("aromasopp, sjampinjong and plain sopp must never be merged", RecipePhotoSkill.Text, StringComparison.Ordinal);
        Assert.DoesNotMatch("aromasopp is Sopp", RecipePhotoSkill.Text);
        // The base-ingredient rules of the architect.
        foreach (var rule in new[] { "småpoteter is Potet", "torskeloin is Torsk", "kremfløte is Fløte", "røde linser and grønne linser", "cayennepepper" })
        {
            Assert.Contains(rule, RecipePhotoSkill.Text, StringComparison.Ordinal);
        }

        Assert.True(RecipePhotoSkill.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 6000);
    }

    [Fact]
    public void The_schema_requires_every_field_and_allows_no_extra_ones()
    {
        var schema = RecipePhotoSkill.Schema;
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order().ToArray();
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(properties, required);
        Assert.Equal(
            new[] { "isRecipe", "readable", "title", "servings", "prepMinutes", "cookMinutes", "description", "sourceBook", "rawText", "steps", "ingredients", "picture", "warnings" }
                .Order(StringComparer.Ordinal),
            properties.Order(StringComparer.Ordinal));
    }

    // ---- Where the SDK and the eval may be -------------------------------------------------------------------------------

    [Fact]
    public void Anthropic_SDK_types_stay_in_Infrastructure()
    {
        var offenders = SourceScanner.Load("src")
            .Where(f => !f.Path.StartsWith("src/Infrastructure/", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(f.Code, @"\bAnthropic\b"))
            .Select(f => f.Path)
            .ToList();

        Assert.Empty(offenders);
        // Not vacuous: the SDK really is used in Infrastructure.
        Assert.Contains(SourceScanner.Load("src/Infrastructure"), f => Regex.IsMatch(f.Code, @"\bAnthropic\b") && f.Path.EndsWith("AnthropicRecipePhotoReader.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_SDK_client_is_built_only_from_the_configured_base_url_and_never_retries()
    {
        var reader = SourceScanner.Load("src/Infrastructure").Single(f => f.Path.EndsWith("AnthropicRecipePhotoReader.cs", StringComparison.Ordinal));

        Assert.Contains("BaseUrl = shared.BaseUrl", reader.Code, StringComparison.Ordinal);
        Assert.Contains("MaxRetries = 0", reader.Code, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", reader.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Temperature", reader.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("Thinking =", reader.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_photo_form_is_read_in_memory_never_spooled_to_a_temp_file()
    {
        var limits = Foodplanner.Api.Features.Recipes.PhotoImportEndpoints.FormLimits;
        var body = Foodplanner.Api.Features.Recipes.PhotoImportEndpoints.MaxBodyBytes;

        Assert.True(limits.MemoryBufferThreshold >= body); // the default 64 KB would write file sections to disk
        Assert.Equal(body, limits.MultipartBodyLengthLimit);
        Assert.True(limits.BufferBody);

        // And the endpoint really reads the form with those limits.
        var endpoint = SourceScanner.Load("src/Api").Single(f => f.Path.EndsWith("PhotoImportEndpoints.cs", StringComparison.Ordinal));
        Assert.Contains("new FormFeature(http, FormLimits)", endpoint.Code, StringComparison.Ordinal);
        Assert.True(endpoint.Code.IndexOf("new FormFeature(", StringComparison.Ordinal) < endpoint.Code.IndexOf("ReadFormAsync(cancellationToken)", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_and_the_workflows_never_call_the_eval_script()
    {
        var files = new List<string> { Path.Combine(Root, "scripts", "verify.sh") };
        var workflows = Path.Combine(Root, ".github");
        if (Directory.Exists(workflows))
        {
            files.AddRange(Directory.EnumerateFiles(workflows, "*.*", SearchOption.AllDirectories));
        }

        Assert.All(files.Where(File.Exists), file => Assert.DoesNotContain("recipe-photo-eval", File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public void The_example_sets_are_ignored_by_git_and_nothing_under_them_is_tracked()
    {
        var ignored = File.ReadAllLines(Path.Combine(Root, ".gitignore")).Select(l => l.Trim());
        Assert.Contains("eval-private/", ignored);

        var tracked = GitLsFiles("eval-private");
        if (tracked is not null)
        {
            Assert.Empty(tracked);
        }
    }

    private static string[]? GitLsFiles(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", $"ls-files -- {path}")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no git on the path: the .gitignore rule above is still checked
        }
    }
}
