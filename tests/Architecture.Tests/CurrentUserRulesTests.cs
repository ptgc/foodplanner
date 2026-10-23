using System.Reflection;
using System.Reflection.Emit;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Foodplanner.Architecture.Tests;

/// <summary>
/// Feature code learns who is calling only through ICurrentUser (ADR 0004, T-009). The scanner looks at signatures,
/// fields, locals and every type, field and method token in the IL, so a use inside a lambda body is found too.
/// </summary>
public class CurrentUserRulesTests
{
    private const string FeaturesNamespace = "Foodplanner.Api.Features";
    private const string AuthNamespace = "Foodplanner.Api.Features.Auth";

    [Fact]
    public void Handlers_use_ICurrentUser_only()
    {
        var featureTypes = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                && (ns == FeaturesNamespace || ns.StartsWith(FeaturesNamespace + ".", StringComparison.Ordinal))
                && ns != AuthNamespace && !ns.StartsWith(AuthNamespace + ".", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(featureTypes); // not vacuous: at least the Health feature is scanned
        var offenders = featureTypes.SelectMany(t => ForbiddenDependencies(t).Select(d => $"{t.FullName} -> {d}")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Scanner_finds_forbidden_dependencies_in_every_position()
    {
        Assert.Contains(ForbiddenDependencies(typeof(InIlOnly)), d => d.Contains("ClaimsPrincipal", StringComparison.Ordinal));
        Assert.Contains(ForbiddenDependencies(typeof(InField)), d => d.Contains("SignInManager", StringComparison.Ordinal));
        Assert.Contains(ForbiddenDependencies(typeof(InConstructor)), d => d.Contains("UserManager", StringComparison.Ordinal));
        Assert.Contains(ForbiddenDependencies(typeof(InLambda)), d => d.Contains("ClaimsPrincipal", StringComparison.Ordinal));
        Assert.Empty(ForbiddenDependencies(typeof(Clean)));
    }

    private static bool IsForbidden(Type? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.HasElementType && IsForbidden(type.GetElementType()))
            {
                return true;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(UserManager<>) || definition == typeof(SignInManager<>))
                {
                    return true;
                }

                if (type.GetGenericArguments().Any(IsForbidden))
                {
                    return true;
                }
            }

            if (type == typeof(ClaimsPrincipal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ForbiddenDependencies(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<string>();

        void Check(Type? candidate, string where)
        {
            if (IsForbidden(candidate))
            {
                found.Add($"{candidate!.Name} ({where})");
            }
        }

        Check(type.BaseType, "base type");
        foreach (var contract in type.GetInterfaces())
        {
            Check(contract, "interface");
        }

        foreach (var field in type.GetFields(all))
        {
            Check(field.FieldType, $"field {field.Name}");
        }

        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method is MethodInfo info)
            {
                Check(info.ReturnType, $"return type of {method.Name}");
            }

            foreach (var parameter in method.GetParameters())
            {
                Check(parameter.ParameterType, $"parameter of {method.Name}");
            }

            var body = method.GetMethodBody();
            if (body is null)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                Check(local.LocalType, $"local in {method.Name}");
            }

            foreach (var token in MemberTokens(method, body))
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

                switch (member)
                {
                    case Type referenced:
                        Check(referenced, $"IL of {method.Name}");
                        break;
                    case FieldInfo referencedField:
                        Check(referencedField.DeclaringType, $"IL of {method.Name}");
                        Check(referencedField.FieldType, $"IL of {method.Name}");
                        break;
                    case MethodBase called:
                        Check(called.DeclaringType, $"IL of {method.Name}");
                        if (called is MethodInfo calledInfo)
                        {
                            Check(calledInfo.ReturnType, $"IL of {method.Name}");
                        }

                        foreach (var parameter in called.GetParameters())
                        {
                            Check(parameter.ParameterType, $"IL of {method.Name}");
                        }

                        break;
                }
            }
        }

        foreach (var nested in type.GetNestedTypes(all))
        {
            found.AddRange(ForbiddenDependencies(nested));
        }

        return found.Distinct();
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<int> MemberTokens(MethodBase method, MethodBody body)
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
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
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
                default: // InlineBrTarget, InlineI, InlineString, InlineSig, ShortInlineR
                    i += 4;
                    break;
            }
        }
    }

    // Fixtures for the scanner self-test. They are not in the Features namespace, so the real rule ignores them.
    private sealed class InIlOnly
    {
        public string? Name(object caller) => ((ClaimsPrincipal)caller).Identity?.Name;
    }

    private sealed class InField
    {
        private readonly SignInManager<IdentityUser>? _signIn = null;

        public bool Has => _signIn is not null;
    }

    private sealed class InConstructor(UserManager<IdentityUser> users)
    {
        public int Hash => users.GetHashCode();
    }

    private sealed class InLambda
    {
        public Func<object, string?> Reader() => caller => ((ClaimsPrincipal)caller).Identity?.Name;
    }

    private sealed class Clean
    {
        public string Name(string value) => value.Trim();
    }
}
