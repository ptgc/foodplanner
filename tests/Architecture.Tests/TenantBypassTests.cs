namespace Foodplanner.Architecture.Tests;

/// <summary>
/// Q-006 and CLAUDE.md rule 1: the ITenantResolution implementation is the only code that bypasses the tenant filter
/// or opens a tenant write scope, solution-wide (src and tests). The rules live in <see cref="TenantSourceRules"/>;
/// the tests here run them on the real sources and on mutants, so a rule that stops working fails loudly.
/// </summary>
public class TenantBypassTests
{
    private const string FeatureFile = "src/Api/Features/Recipes/RecipeEndpoints.cs";

    private static List<SourceFile> Solution() => SourceScanner.Load("src", "tests");

    [Fact]
    public void Tenant_resolution_is_the_only_bypass_in_src_and_tests()
    {
        var files = Solution();

        Assert.Contains(files, f => f.Path.StartsWith("src/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Path.StartsWith("tests/", StringComparison.Ordinal));
        var resolution = Assert.Single(files, f => f.Path == TenantSourceRules.TenantResolutionFile);
        Assert.Contains("IgnoreQueryFilters", resolution.Code, StringComparison.Ordinal); // not vacuous
        Assert.Contains("ForHousehold", resolution.Code, StringComparison.Ordinal);

        Assert.Empty(TenantSourceRules.Bypasses(files));
    }

    [Fact]
    public void Bypass_allow_list_has_no_stale_entries_and_only_test_support_besides_resolution()
    {
        Assert.Empty(TenantSourceRules.StaleAllowances(Solution()));
        Assert.All(
            TenantSourceRules.Allowed.Keys.Where(k => k != TenantSourceRules.TenantResolutionFile),
            path => Assert.StartsWith("tests/", path, StringComparison.Ordinal));
        Assert.All(TenantSourceRules.Allowed.Values, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
    }

    [Fact]
    public void Unnamed_IgnoreQueryFilters_is_rejected_everywhere_including_allowed_files()
    {
        const string unnamed = """
            class Q { void M(Db db) { db.Recipes.IgnoreQueryFilters().ToList(); } }
            """;

        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, unnamed)]));
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, unnamed)]));
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Persistence/TenancyWriteGuardTests.cs", unnamed)]));

        // Across lines, as a method group, and as a static call.
        var multiline = "class Q { void M() { x.IgnoreQueryFilters(\n  ); } }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, multiline)]));
        const string methodGroup = "class Q { void M() { var f = x.IgnoreQueryFilters; } }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, methodGroup)]));
        const string staticCall = "class Q { void M() { EntityFrameworkQueryableExtensions.IgnoreQueryFilters(x); } }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, staticCall)]));
    }

    [Fact]
    public void Named_IgnoreQueryFilters_outside_the_resolution_is_rejected()
    {
        const string named = """
            class Q { void M(Db db) { db.Recipes.IgnoreQueryFilters([AppDbContext.TenantFilterName]).ToList(); } }
            """;

        var finding = Assert.Single(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, named)]));
        Assert.Equal(FeatureFile, finding.File);
        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, named)]));
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Anything.cs", named)]));
    }

    [Fact]
    public void ForHousehold_outside_the_resolution_and_allow_listed_test_support_is_rejected()
    {
        const string call = """
            class Q { void M(ITenantWriteScope scope) { using var s = scope.ForHousehold(id); } }
            """;

        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, call)]));
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Anything.cs", call)]));
        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, call)]));
        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Persistence/TenancyWriteGuardTests.cs", call)]));

        // Allowed ForHousehold does not license IgnoreQueryFilters in the same test file.
        const string both = "class Q { void M() { x.IgnoreQueryFilters([\"Tenant\"]); } }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Persistence/TenancyWriteGuardTests.cs", both)]));
    }

    [Fact]
    public void Interpolation_holes_are_code_and_src_string_literals_are_scanned()
    {
        // A call inside an interpolation hole is still a call.
        const string hole = "class Q { string M(Db db) => $\"rows: {db.Notes.IgnoreQueryFilters().Count()} {{literal}} {(true ? \"a\" : \"b\")}\"; }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, hole)]));

        // In src a name inside a string literal can feed reflection or an expression tree; in tests it cannot.
        const string reflective = "class Q { object M(Type t) => t.GetMethod(\"IgnoreQueryFilters\"); }";
        Assert.NotEmpty(TenantSourceRules.Bypasses([SourceFile.FromText(FeatureFile, reflective)]));
        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Anything.cs", reflective)]));

        // Code after an interpolated string is still lexed as code, and text in the string is not.
        const string after = "class Q { void M() { var s = $\"{x}\"; var t = \"IgnoreQueryFilters()\"; } }";
        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Anything.cs", after)]));
    }

    [Fact]
    public void Declaring_ForHousehold_and_mentions_in_comments_or_strings_are_not_calls()
    {
        const string harmless = """
            interface IScope { IDisposable ForHousehold(Guid id); }
            // scope.ForHousehold(id) and x.IgnoreQueryFilters() are discussed here
            /* x.IgnoreQueryFilters() */
            class Q { string s = "x.IgnoreQueryFilters() scope.ForHousehold(id)"; string v = @"a ""b"" ForHousehold("; char c = '"'; }
            """;

        Assert.Empty(TenantSourceRules.Bypasses([SourceFile.FromText("tests/Api.Tests/Anything.cs", harmless)]));
    }

    [Fact]
    public void SetProperty_of_HouseholdId_is_rejected_in_src()
    {
        var src = SourceScanner.Load("src");
        Assert.Contains(src, f => f.CodeWithStrings.Contains("SetProperty", StringComparison.Ordinal)); // not vacuous
        Assert.Empty(TenantSourceRules.HouseholdIdSetters(src));

        string[] mutants =
        [
            "class Q { Task M(Db d) => d.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.HouseholdId, other)); }",
            "class Q { Task M(Db d) => d.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.Text, \"x\").SetProperty(n => EF.Property<Guid>(n, \"HouseholdId\"), other)); }",
            "class Q { Task M(Db d) => d.Notes.ExecuteUpdateAsync(s => s\n.SetProperty(n => n.Text, \"x\")\n.SetProperty(n => EF.Property<Guid>(n, nameof(ITenantEntity.HouseholdId)), other)); }",
        ];
        foreach (var mutant in mutants)
        {
            Assert.NotEmpty(TenantSourceRules.HouseholdIdSetters([SourceFile.FromText(FeatureFile, mutant)]));
        }

        // A non-literal column name could be HouseholdId at run time.
        const string dynamicName = "class Q { Task M(Db d, string column) => d.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => EF.Property<Guid>(n, column), other)); }";
        Assert.NotEmpty(TenantSourceRules.HouseholdIdSetters([SourceFile.FromText(FeatureFile, dynamicName)]));
        const string literalName = "class Q { Task M(Db d) => d.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => EF.Property<DateTimeOffset>(n, \"UpdatedAt\"), now).SetProperty(n => EF.Property<DateTimeOffset>(n, TenancyModelConventions.UpdatedAt), now)); }";
        Assert.Empty(TenantSourceRules.HouseholdIdSetters([SourceFile.FromText(FeatureFile, literalName)]));

        const string fine = "class Q { Task M(Db d) => d.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.Text, n => n.Text + \"x\")); }";
        Assert.Empty(TenantSourceRules.HouseholdIdSetters([SourceFile.FromText(FeatureFile, fine)]));
    }

    [Fact]
    public void ExecuteUpdate_and_ExecuteDelete_are_not_used_in_src_outside_the_resolution()
    {
        var src = SourceScanner.Load("src");
        Assert.Contains(src, f => f.Path == TenantSourceRules.TenantResolutionFile && f.Code.Contains("ExecuteUpdate", StringComparison.Ordinal)); // not vacuous
        Assert.Empty(TenantSourceRules.BulkWrites(src));
        Assert.All(TenantSourceRules.BulkWriteAllowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Value)));

        string[] mutants =
        [
            "class Q { Task M(Db d) => d.Ingredients.ExecuteDeleteAsync(); }",
            "class Q { Task M(Db d) => d.Ingredients.ExecuteUpdateAsync(s => s.SetProperty(i => i.Name, \"x\")); }",
            "class Q { int M(Db d) => d.Ingredients.ExecuteDelete(); }",
        ];
        foreach (var mutant in mutants)
        {
            Assert.NotEmpty(TenantSourceRules.BulkWrites([SourceFile.FromText(FeatureFile, mutant)]));
        }

        Assert.Empty(TenantSourceRules.BulkWrites([SourceFile.FromText(TenantSourceRules.TenantResolutionFile, mutants[0])]));
    }

    [Fact]
    public void Raw_SQL_is_not_used_in_production_code_outside_migrations()
    {
        // The exemption is pinned to the one generated-migrations folder, not any folder named Migrations.
        var production = SourceScanner.Load("src")
            .Where(f => !f.Path.StartsWith("src/Infrastructure/Persistence/Migrations/", StringComparison.Ordinal)).ToList();
        Assert.Contains(production, f => f.Path == TenantSourceRules.TenantResolutionFile); // not vacuous
        Assert.Empty(TenantSourceRules.RawSqlUse(production));

        string[] mutants =
        [
            "class Q { Task M(Db d) => d.Database.ExecuteSqlRawAsync(\"DELETE FROM Notes\"); }",
            "class Q { Task M(Db d) => d.Database.ExecuteSqlAsync($\"DELETE FROM Notes\"); }",
            "class Q { void M(Db d) { d.Database.SqlQueryRaw<int>(\"SELECT 1\"); } }",
            "class Q { void M(Db d) { d.Notes.FromSqlInterpolated($\"SELECT * FROM Notes\"); } }",
            "class Q { void M(Db d) { d.Database.GetDbConnection(); } }",
            "class Q { void M() { using var c = new SqlConnection(cs); } }",
            "class Q { void M(DbDataSource s) { s.CreateCommand(\"x\"); } }",
            "class Q { void M(IDbConnection c) { } }",
            "class Q { void M(IDbCommand c) { } }",
            "class Q { void M(DbBatch b) { } }",
            "class Q { void M() { new SqlBulkCopy(cs); } }",
            "class Q { void M(ModelBuilder b) { b.Entity<X>().ToSqlQuery(\"x\"); } }",
            "class Q { void M(ModelBuilder b) { b.HasDbFunction(typeof(Q)); } }",
            "class Q { void M(ModelBuilder b) { b.Entity<X>().ToFunction(\"f\"); } }",
            "class Q { void M(DbSet<X> s) { s.FromExpression(() => null); } }",
            "class Q { void M(Db d) { var s = $\"{d.Database.ExecuteSqlRaw(\"x\")}\"; } }",
            "class Q { void M(Db d) { d.GetType().GetMethod(\"ExecuteSqlRaw\"); } }",
        ];
        foreach (var mutant in mutants)
        {
            Assert.NotEmpty(TenantSourceRules.RawSqlUse([SourceFile.FromText(FeatureFile, mutant)]));
        }
    }
}
