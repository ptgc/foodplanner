namespace Foodplanner.Architecture.Tests;

/// <summary>
/// T-026: kid mode and the week lock are enforced by policies, not by endpoint code. Source rules, run on the real sources.
/// </summary>
public class KidModeRulesTests
{
    private static List<SourceFile> Api() => SourceScanner.Load("src/Api");

    [Fact]
    public void Week_policy_is_the_only_lock_check()
    {
        var files = Api();
        var endpoints = Assert.Single(files, f => f.Path == "src/Api/Features/MealPlans/MealPlanEndpoints.cs");

        // The lock state is read only by the policy handler (authorization) and the meal plan endpoint file (the read mapping,
        // the lock/unlock endpoints). No other feature code looks at it.
        var readers = files
            .Where(f => f.Code.Contains("LockedAt", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(f.Code, @"\.IsLocked\b(?!\()"))
            .Select(f => f.Path)
            .Order()
            .ToList();
        Assert.Equal(["src/Api/Authorization/MealPlanEntryAccess.cs", "src/Api/Features/MealPlans/MealPlanEndpoints.cs"], readers);

        // Every plan write asks the policy: add, change and delete an entry.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(endpoints.Code, @"week\.CanWriteAsync\(plan\)").Count);
    }

    [Fact]
    public void The_kid_claims_are_read_by_auth_code_and_the_household_resolution_only()
    {
        var offenders = Api()
            .Where(f => f.Code.Contains("KidModeClaims", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Where(p => !p.StartsWith("src/Api/Features/Auth/", StringComparison.Ordinal) && p != "src/Api/Tenancy/HouseholdMembershipGuard.cs")
            .ToList();
        Assert.Empty(offenders);
        Assert.Contains(Api(), f => f.Path == "src/Api/Features/Auth/KidModeClaims.cs"); // not vacuous
    }

    [Fact]
    public void Feature_code_never_compares_the_membership_role_to_decide_kid_access()
    {
        // Kid mode narrows the role in ONE place (HouseholdRoles.Effective); features only use the result through policies.
        var users = Api().Where(f => f.Path.StartsWith("src/Api/Features/", StringComparison.Ordinal)
            && !f.Path.StartsWith("src/Api/Features/Auth/", StringComparison.Ordinal))
            .Where(f => f.Code.Contains("fp:profile", StringComparison.Ordinal) || f.CodeWithStrings.Contains("\"fp:profile", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(users);
    }
}
