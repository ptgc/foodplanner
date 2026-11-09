using System.Text.Json;
using Foodplanner.Infrastructure.Import.Ai;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// T-077 / Q-028: the AI fallback is off by default everywhere and no API key is ever in the repo. It is switched on only by
/// configuration outside the repo (user secrets, Key Vault).
/// </summary>
public class AiImportConfigRulesTests
{
    [Fact]
    public void The_options_default_to_off_with_no_key()
    {
        var options = new AiImportOptions();

        Assert.False(options.Enabled);
        Assert.Null(options.ApiKey);
        Assert.False(options.IsActive);
        Assert.Equal("claude-haiku-5-5", options.Model);
        Assert.Equal(20, options.DailyCallsPerHousehold);
        Assert.Equal(200, options.MonthlyCallsPerHousehold);
    }

    [Fact]
    public void A_key_alone_or_the_flag_alone_does_not_turn_it_on()
    {
        Assert.False(new AiImportOptions { Enabled = true }.IsActive);
        Assert.False(new AiImportOptions { Enabled = true, ApiKey = "  " }.IsActive);
        Assert.False(new AiImportOptions { ApiKey = "k" }.IsActive);
        Assert.True(new AiImportOptions { Enabled = true, ApiKey = "k" }.IsActive);
    }

    [Fact]
    public void Committed_appsettings_files_say_off_and_hold_no_key()
    {
        var root = SourceScanner.SolutionRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Api"), "appsettings*.json").ToList();
        Assert.Contains(files, f => Path.GetFileName(f) == "appsettings.json");

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("AiImport", out var section))
            {
                continue;
            }

            Assert.False(section.TryGetProperty("ApiKey", out _), $"{Path.GetFileName(file)} must not hold an API key.");
            if (section.TryGetProperty("Enabled", out var enabled))
            {
                Assert.Equal(JsonValueKind.False, enabled.ValueKind);
            }
        }

        // appsettings.json spells the default out, so a reader sees it is off.
        using var main = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Api", "appsettings.json")));
        Assert.Equal(JsonValueKind.False, main.RootElement.GetProperty("AiImport").GetProperty("Enabled").ValueKind);
    }

    [Fact]
    public void Compose_and_workflows_do_not_enable_the_ai_fallback_or_carry_a_key()
    {
        var root = SourceScanner.SolutionRoot();
        var candidates = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).StartsWith("docker-compose", StringComparison.Ordinal))
            .Concat(Directory.Exists(Path.Combine(root, ".github")) ? Directory.EnumerateFiles(Path.Combine(root, ".github"), "*.yml", SearchOption.AllDirectories) : []);
        foreach (var file in candidates)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("AiImport__ApiKey", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"AiImport__Enabled\s*[:=]\s*[""']?true", text);
        }
    }

    [Fact]
    public void Api_test_hosts_come_from_TestApiFactory_which_forces_the_ai_fallback_off()
    {
        var files = SourceScanner.Load("tests");
        var factory = Assert.Single(files, f => f.Path == "tests/Api.Tests/TestApiFactory.cs");
        Assert.Contains("AiImport:Enabled", factory.CodeWithStrings, StringComparison.Ordinal); // not vacuous
        Assert.Contains("NoRealAiClient", factory.Code, StringComparison.Ordinal);

        Assert.Empty(BareHostFactories(files.Where(f => f.Path != factory.Path)));

        // The rule catches the shapes a test would use.
        Assert.NotEmpty(BareHostFactories([SourceFile.FromText("tests/Api.Tests/X.cs", "class X { void M() { var f = new WebApplicationFactory<Program>(); } }")]));
        Assert.NotEmpty(BareHostFactories([SourceFile.FromText("tests/Api.Tests/X.cs", "class X : IClassFixture<WebApplicationFactory<Program>> { }")]));
        Assert.NotEmpty(BareHostFactories([SourceFile.FromText("tests/Api.Tests/X.cs", "class F : WebApplicationFactory<Program> { }")]));
        Assert.Empty(BareHostFactories([SourceFile.FromText("tests/Api.Tests/X.cs", "class X { void M() { var f = new TestApiFactory(); } }")]));
    }

    private static List<string> BareHostFactories(IEnumerable<SourceFile> files) =>
        [.. files
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                f.Code, @"new\s+WebApplicationFactory\s*<|IClassFixture\s*<\s*WebApplicationFactory\s*<|:\s*WebApplicationFactory\s*<"))
            .Select(f => f.Path)];
}
