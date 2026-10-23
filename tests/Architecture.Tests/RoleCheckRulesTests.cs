using System.Reflection;
using System.Reflection.Emit;
using System.Security.Claims;
using System.Security.Principal;
using Foodplanner.Domain.Tenancy;
using Foodplanner.Infrastructure.Persistence.Tenancy;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// Authorization is policy-based (CLAUDE.md rule 2, T-011): feature code never reads the caller's household role or
/// calls IsInRole. Only the authorization handler and the ICurrentHousehold implementation do. The scanner walks every
/// method body of every type under Foodplanner.Api.Features, including lambdas and compiler-generated types.
/// </summary>
public class RoleCheckRulesTests
{
    private const string FeaturesNamespace = "Foodplanner.Api.Features";

    [Fact]
    public void No_role_checks_outside_policies()
    {
        var featureTypes = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && (ns == FeaturesNamespace || ns.StartsWith(FeaturesNamespace + ".", StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(featureTypes);
        Assert.Contains(featureTypes, t => t.Name == "HouseholdEndpoints"); // not vacuous: the feature that has a policy is scanned
        var offenders = featureTypes
            .SelectMany(t => RoleChecks(t).Where(c => !IsAllowedMembershipListing(c)).Select(c => $"{t.FullName} -> {c}"))
            .ToList();
        Assert.Empty(offenders);
    }

    // Gap h. GetMembershipsForUser returns the caller's role in every household. Feature code must not use it to decide
    // anything; the one legitimate use is echoing the list back in ListHouseholds. A call site elsewhere is flagged.
    // What the allowed method does with the roles it gets back cannot be checked by this scanner (review catches it).
    private static readonly string AllowedMembershipListing = typeof(Foodplanner.Api.Features.Households.HouseholdEndpoints).FullName + "+<ListHouseholds>";

    private static bool IsAllowedMembershipListing(string check) =>
        check.StartsWith(nameof(ITenantResolution) + "." + nameof(ITenantResolution.GetMembershipsForUser), StringComparison.Ordinal)
        && check.Contains("type " + AllowedMembershipListing, StringComparison.Ordinal);

    [Fact]
    public void Membership_listing_is_used_only_by_ListHouseholds()
    {
        var endpoints = typeof(Foodplanner.Api.Features.Households.HouseholdEndpoints);

        // Not vacuous: the one allowed call site exists, and the scanner sees it.
        Assert.Contains(RoleChecks(endpoints), IsAllowedMembershipListing);
        Assert.Contains(RoleChecks(typeof(ListsMemberships)), c => c.Contains("GetMembershipsForUser", StringComparison.Ordinal));
        Assert.DoesNotContain(RoleChecks(typeof(ListsMemberships)), IsAllowedMembershipListing);
    }

    [Fact]
    public void Scanner_finds_role_reads_and_IsInRole()
    {
        Assert.Contains(RoleChecks(typeof(ReadsRole)), c => c.Contains("get_Role", StringComparison.Ordinal));
        Assert.Contains(RoleChecks(typeof(ReadsRoleInLambda)), c => c.Contains("get_Role", StringComparison.Ordinal));
        Assert.Contains(RoleChecks(typeof(CallsIsInRole)), c => c.Contains("IsInRole", StringComparison.Ordinal));
        Assert.Contains(RoleChecks(typeof(CallsIsInRoleOnPrincipal)), c => c.Contains("IsInRole", StringComparison.Ordinal));
        Assert.Contains(RoleChecks(typeof(LooksUpRole)), c => c.Contains("FindRole", StringComparison.Ordinal));
        Assert.Empty(RoleChecks(typeof(ReadsHouseholdId)));
    }

    private static bool IsRoleCheck(MemberInfo member) =>
        // Looking a role up by hand is a role check too, even without reading ICurrentHousehold.Role.
        ((member.Name == nameof(ITenantResolution.FindRole) || member.Name == nameof(ITenantResolution.GetMembershipsForUser))
            && member.DeclaringType == typeof(ITenantResolution))
        || (member.Name == "get_Role" && member.DeclaringType is { } type
            && (type == typeof(ICurrentHousehold) || type.GetInterfaces().Contains(typeof(ICurrentHousehold))))
        || (member.Name == "IsInRole" && member.DeclaringType is { } principal
            && (principal == typeof(ClaimsPrincipal) || principal == typeof(IPrincipal)));

    private static IEnumerable<string> RoleChecks(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<string>();
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                continue;
            }

            foreach (var token in MethodTokens(body))
            {
                MemberInfo? member;
                try
                {
                    member = method.Module.ResolveMember(
                        token,
                        type.IsGenericType ? type.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (member is MethodBase called && IsRoleCheck(called))
                {
                    found.Add($"{called.DeclaringType!.Name}.{called.Name} (in {method.Name}, type {type.FullName})");
                }
            }
        }

        foreach (var nested in type.GetNestedTypes(all))
        {
            found.AddRange(RoleChecks(nested));
        }

        return found.Distinct();
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<int> MethodTokens(MethodBody body)
    {
        var il = body.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        for (var i = 0; i < il.Length;)
        {
            short value = il[i++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[i++]);
            }

            var opCode = OpCodesByValue[value];
            switch (opCode.OperandType)
            {
                case OperandType.InlineMethod:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, i);
                    i += 4 + (4 * count);
                    break;
                default: // InlineField, InlineType, InlineTok, InlineBrTarget, InlineI, InlineString, InlineSig, ShortInlineR
                    i += 4;
                    break;
            }
        }
    }

    // Fixtures for the scanner self-test. They are not in the Features namespace, so the real rule ignores them.
    private sealed class ReadsRole
    {
        public bool IsOwner(ICurrentHousehold household) => household.Role == HouseholdRole.Owner;
    }

    private sealed class ReadsRoleInLambda
    {
        public Func<ICurrentHousehold, bool> Check() => household => household.Role is HouseholdRole.Adult;
    }

    private sealed class CallsIsInRole
    {
        public bool Is(ClaimsPrincipal user) => user.IsInRole("Owner");
    }

    private sealed class CallsIsInRoleOnPrincipal
    {
        public bool Is(IPrincipal user) => user.IsInRole("Owner");
    }

    private sealed class LooksUpRole
    {
        public Task<HouseholdRole?> Role(ITenantResolution resolution) => resolution.FindRole(Guid.Empty, Guid.Empty, default);
    }

    private sealed class ListsMemberships
    {
        public Task<IReadOnlyList<HouseholdMembership>> Mine(ITenantResolution resolution) => resolution.GetMembershipsForUser(Guid.Empty, default);
    }

    private sealed class ReadsHouseholdId
    {
        public Guid? Id(ICurrentHousehold household) => household.HouseholdId;
    }
}
