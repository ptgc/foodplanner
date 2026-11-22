using System.Text.RegularExpressions;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// T-096, system admin support: the rules that keep the most sensitive path of the app narrow. Source rules over the real
/// sources. The endpoint-table rules (every support endpoint has a policy and an audit declaration) are in Api.Tests, where the
/// host can be built (<c>SupportEndpointRulesTests</c>).
/// </summary>
public partial class SupportRulesTests
{
    private const string FeaturesFolder = "src/Api/Features/";

    private static List<SourceFile> Src() => SourceScanner.Load("src");

    private static IEnumerable<SourceFile> Features(List<SourceFile> files) =>
        files.Where(f => f.Path.StartsWith(FeaturesFolder, StringComparison.Ordinal));

    [GeneratedRegex(@"\bIgnoreQueryFilters\b")]
    private static partial Regex Bypass();

    [GeneratedRegex(@"\bISystemAdminDirectory\b|\bISupportContext\b|\bSupportSessions?\b|\bSupportAuditEntr(y|ies)\b|\bSupportContextMiddleware\b|\bRequestSupportContext\b|\bSupportSessionInfo\b")]
    private static partial Regex ForbiddenInFeatures();

    [GeneratedRegex(@"\bSystemAdminEmails\b")]
    private static partial Regex AdminList();

    [GeneratedRegex(@"\bHouseholdRole\.(\w+)")]
    private static partial Regex RoleLiteral();

    [Fact]
    public void Support_adds_no_filter_bypass()
    {
        var files = Src().Where(f => !f.Path.Contains("/Migrations/", StringComparison.Ordinal)).ToList();

        // The bypass lives in TenantResolution.cs only, with the same number of call sites as before T-096 (19 on dev).
        var resolution = Assert.Single(files, f => f.Path == TenantSourceRules.TenantResolutionFile);
        Assert.Equal(19, Bypass().Matches(resolution.CodeWithStrings).Count);
        Assert.Equal([TenantSourceRules.TenantResolutionFile], files.Where(f => Bypass().IsMatch(f.CodeWithStrings)).Select(f => f.Path).ToArray());

        // The allow-list of files that may use a bypass or a write scope is the pre-existing one.
        Assert.Equal(
            [
                TenantSourceRules.TenantResolutionFile,
                "tests/Api.Tests/Persistence/TenancyWriteGuardTests.cs",
                "tests/Api.Tests/Seeding/LocalAdminSeederTests.cs",
            ],
            TenantSourceRules.Allowed.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(TenantSourceRules.Bypasses(files));
        Assert.Empty(TenantSourceRules.BulkWrites(files));
    }

    [Fact]
    public void The_membership_listing_has_exactly_the_two_named_callers()
    {
        var callers = Src()
            .Where(f => f.Code.Contains("GetMembershipsForUser", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The interface, the implementation, ListHouseholds, SupportUserSearch (T-096), and the local seeder (Development only).
        Assert.Equal(
            [
                "src/Api/Features/Households/HouseholdEndpoints.cs",
                "src/Api/Features/Support/SupportEndpoints.cs",
                "src/Api/Seeding/LocalAdminSeeder.cs",
                "src/Infrastructure/Persistence/Tenancy/ITenantResolution.cs",
                "src/Infrastructure/Persistence/Tenancy/TenantResolution.cs",
            ],
            callers);
    }

    [Fact]
    public void Feature_code_never_reads_admin_status_or_the_session_table()
    {
        var files = Src();
        var offenders = Features(files)
            .Where(f => ForbiddenInFeatures().IsMatch(f.Code))
            .Select(f => $"{f.Path}: {ForbiddenInFeatures().Match(f.Code).Value}")
            .ToList();
        Assert.Empty(offenders);

        // Not vacuous: the policy code and the middleware, which may read them, do.
        Assert.Contains(files, f => f.Path == "src/Api/Authorization/SupportPolicies.cs" && ForbiddenInFeatures().IsMatch(f.Code));
        Assert.Contains(files, f => f.Path == "src/Api/Support/SupportContextMiddleware.cs" && ForbiddenInFeatures().IsMatch(f.Code));

        // The one feature-code route to "am I an admin" is the /me answer; nothing else asks.
        Assert.Equal(
            [FeaturesFolder + "Auth/AuthEndpoints.cs"],
            Features(files).Where(f => f.Code.Contains("ISystemAdminStatus", StringComparison.Ordinal)).Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Nothing_writes_the_admin_allow_list()
    {
        // The list is configuration, read only by the options class, its validator and the registration. No endpoint, handler or
        // service names it, so nothing in the app can grant or change admin status.
        var files = Src().Where(f => AdminList().IsMatch(f.CodeWithStrings)).Select(f => f.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/Api/Support/SupportOptions.cs", "src/Api/Support/SupportSetup.cs", "src/Api/Support/SystemAdminDirectory.cs"], files);

        var setters = Src().Where(f => f.Code.Contains("SupportOptions", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(f.Code, @"SupportOptions[^;]{0,80}\.\s*SystemAdminEmails\s*="))
            .ToList();
        Assert.Empty(setters);
    }

    [Fact]
    public void Support_actions_reuse_the_household_code_and_name_no_role_but_Owner()
    {
        var files = Src();
        var support = Assert.Single(files, f => f.Path == FeaturesFolder + "Support/SupportHouseholdEndpoints.cs");

        // The four fixes call the same application methods as the Owner and Adult endpoints.
        foreach (var shared in new[]
                 {
                     "HouseholdMemberEndpoints.RevokeOpenInviteAsync",
                     "HouseholdMemberEndpoints.ApplyRoleAsync",
                     "HouseholdManagementEndpoints.LockHouseholdAsync",
                     "pins.ClearLockoutAsync",
                     "MealPlanEndpoints.UnlockWeekRowAsync",
                 })
        {
            Assert.Contains(shared, support.Code, StringComparison.Ordinal);
        }

        Assert.Contains("RevokeOpenInviteAsync(db, inviteId", Assert.Single(files, f => f.Path == FeaturesFolder + "Households/HouseholdMemberEndpoints.cs").Code, StringComparison.Ordinal);
        Assert.Contains("UnlockWeekRowAsync", Assert.Single(files, f => f.Path == FeaturesFolder + "MealPlans/MealPlanEndpoints.cs").Code, StringComparison.Ordinal);

        // No role literal but Owner, and no ordering of roles (Satisfies/Effective): support never demotes, removes or acts as a role.
        Assert.All(
            files.Where(f => f.Path.StartsWith(FeaturesFolder + "Support/", StringComparison.Ordinal)),
            f =>
            {
                Assert.All(RoleLiteral().Matches(f.Code).Select(m => m.Groups[1].Value), role => Assert.Equal("Owner", role));
                Assert.DoesNotContain("Satisfies", f.Code, StringComparison.Ordinal);
                Assert.DoesNotContain("Effective(", f.Code, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void The_support_policies_are_the_only_readers_of_the_session_state()
    {
        var files = Src();
        var readers = files
            .Where(f => Regex.IsMatch(f.Code, @"\bISupportContext\b") && !f.Path.StartsWith("src/Api/Support/", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["src/Api/Authorization/SupportPolicies.cs"], readers);
    }

    [Fact]
    public void Support_never_sets_the_role_and_the_session_household_comes_from_the_session_row()
    {
        var files = Src();

        // SetSupport is called with the SESSION's household in the middleware, and nowhere else but the Owner notifier, which only
        // widens nothing: it runs after the commit of an action of an already authorised session.
        var callers = files.Where(f => f.Code.Contains(".SetSupport(", StringComparison.Ordinal)).Select(f => f.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["src/Api/Support/SupportAudit.cs", "src/Api/Support/SupportContextMiddleware.cs"], callers);

        var middleware = Assert.Single(files, f => f.Path == "src/Api/Support/SupportContextMiddleware.cs");
        Assert.Contains("household.SetSupport(session.HouseholdId, session.Id)", middleware.Code, StringComparison.Ordinal);

        // No support handler binds the route's household id.
        Assert.All(
            files.Where(f => f.Path.StartsWith(FeaturesFolder + "Support/", StringComparison.Ordinal)),
            f => Assert.DoesNotContain("Guid targetHouseholdId", f.Code, StringComparison.Ordinal));
    }
}
