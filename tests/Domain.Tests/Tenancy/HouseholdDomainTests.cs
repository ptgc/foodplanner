using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.Tenancy;

public class HouseholdDomainTests
{
    [Theory]
    [InlineData(HouseholdRole.Owner, HouseholdRole.Owner, true)]
    [InlineData(HouseholdRole.Owner, HouseholdRole.Adult, true)]
    [InlineData(HouseholdRole.Owner, HouseholdRole.Child, true)]
    [InlineData(HouseholdRole.Adult, HouseholdRole.Owner, false)]
    [InlineData(HouseholdRole.Adult, HouseholdRole.Adult, true)]
    [InlineData(HouseholdRole.Adult, HouseholdRole.Child, true)]
    [InlineData(HouseholdRole.Child, HouseholdRole.Owner, false)]
    [InlineData(HouseholdRole.Child, HouseholdRole.Adult, false)]
    [InlineData(HouseholdRole.Child, HouseholdRole.Child, true)]
    public void Roles_are_hierarchical(HouseholdRole actual, HouseholdRole required, bool expected) =>
        Assert.Equal(expected, actual.Satisfies(required));

    [Fact]
    public void Undefined_role_satisfies_nothing()
    {
        Assert.False(((HouseholdRole)0).Satisfies(HouseholdRole.Child));
        Assert.False(((HouseholdRole)42).Satisfies(HouseholdRole.Child));
    }

    [Theory]
    [InlineData("Adult", true)]
    [InlineData("child", true)]
    [InlineData("Owner", true)]
    [InlineData("Boss", false)]
    [InlineData("2", false)]
    [InlineData(" Adult", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Role_parse_is_strict(string? value, bool parsed) =>
        Assert.Equal(parsed, HouseholdRoles.TryParse(value, out _));

    [Fact]
    public void Only_adult_and_child_are_invitable()
    {
        Assert.True(HouseholdRole.Adult.IsInvitable());
        Assert.True(HouseholdRole.Child.IsInvitable());
        Assert.False(HouseholdRole.Owner.IsInvitable());
        Assert.False(((HouseholdRole)9).IsInvitable());
    }

    [Fact]
    public void Role_serializes_as_a_string() =>
        Assert.Equal("\"Owner\"", JsonSerializer.Serialize(HouseholdRole.Owner));

    [Theory]
    [InlineData("  Smith family ", "Smith family")]
    [InlineData("A", "A")]
    public void Household_name_is_trimmed(string input, string expected) =>
        Assert.Equal(expected, new Household(input).Name);

    [Fact]
    public void Household_name_length_bounds()
    {
        Assert.NotNull(Household.NormalizeName(new string('x', 100)));
        Assert.Null(Household.NormalizeName(new string('x', 101)));
        Assert.Null(Household.NormalizeName("   "));
        Assert.Null(Household.NormalizeName(""));
        Assert.Null(Household.NormalizeName(null));
        Assert.Throws<ArgumentException>(() => new Household(" "));
    }

    [Fact]
    public void Invite_cannot_grant_owner()
    {
        var hash = InviteToken.Hash(InviteToken.Generate());
        Assert.Throws<ArgumentException>(() => new Invite(Guid.NewGuid(), hash, HouseholdRole.Owner, DateTimeOffset.UtcNow, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new Invite(Guid.Empty, hash, HouseholdRole.Adult, DateTimeOffset.UtcNow, Guid.NewGuid()));
    }

    [Fact]
    public void Invite_is_usable_only_before_expiry_and_before_acceptance()
    {
        var now = DateTimeOffset.UtcNow;
        var invite = new Invite(Guid.NewGuid(), InviteToken.Hash(InviteToken.Generate()), HouseholdRole.Child, now.AddDays(7), Guid.NewGuid());

        Assert.True(invite.IsUsable(now));
        Assert.False(invite.IsUsable(now.AddDays(7)));
        Assert.False(invite.IsUsable(now.AddDays(8)));
    }

    [Fact]
    public void Membership_requires_a_household_and_a_defined_role()
    {
        Assert.Throws<ArgumentException>(() => new Membership(Guid.Empty, Guid.NewGuid(), HouseholdRole.Adult));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Membership(Guid.NewGuid(), Guid.NewGuid(), (HouseholdRole)0));
    }

    [Fact]
    public void Tokens_are_32_random_bytes_base64url()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => InviteToken.Generate()).ToList();

        Assert.Equal(50, tokens.Distinct().Count());
        Assert.All(tokens, token =>
        {
            Assert.Equal(InviteToken.EncodedLength, token.Length);
            Assert.True(InviteToken.HasValidShape(token));
            Assert.DoesNotContain('=', token);
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.Equal(InviteToken.ByteLength, Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length);
        });
    }

    [Fact]
    public void Token_hash_is_sha256_of_the_token()
    {
        var token = InviteToken.Generate();

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), InviteToken.Hash(token));
        Assert.NotEqual(Encoding.UTF8.GetBytes(token), InviteToken.Hash(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 44 characters
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")] // 43 characters, bad character
    public void Malformed_tokens_have_no_valid_shape(string? token) => Assert.False(InviteToken.HasValidShape(token));
}
