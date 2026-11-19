using Foodplanner.Domain.Households;

namespace Foodplanner.Domain.Tests.Households;

public class MemberProfileTests
{
    [Fact]
    public void A_profile_has_a_trimmed_name_and_an_avatar_from_the_fixed_set()
    {
        var profile = new MemberProfile("  Mia   Lou ", "fox");

        Assert.Equal("Mia Lou", profile.DisplayName);
        Assert.Equal("fox", profile.Avatar);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcde")] // 31
    [InlineData("bell\u0007name")]
    public void Names_must_be_1_to_30_characters_without_control_characters(string? name)
    {
        Assert.Null(MemberProfile.NormalizeName(name));
        Assert.Throws<ArgumentException>(() => new MemberProfile(name!, "fox"));
    }

    [Fact]
    public void A_name_of_30_characters_is_fine()
    {
        Assert.Equal(30, MemberProfile.NormalizeName(new string('x', 30))!.Length);
    }

    [Theory]
    [InlineData("dragon")]
    [InlineData("Fox")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_avatars_are_refused(string? avatar)
    {
        Assert.False(ProfileAvatars.IsValid(avatar));
        Assert.Throws<ArgumentException>(() => new MemberProfile("Mia", avatar!));
    }

    [Fact]
    public void All_twelve_avatars_are_valid_and_distinct()
    {
        Assert.Equal(12, ProfileAvatars.All.Count);
        Assert.Equal(12, ProfileAvatars.All.Distinct().Count());
        Assert.All(ProfileAvatars.All, a => Assert.True(ProfileAvatars.IsValid(a)));
    }

    [Fact]
    public void Change_validates_like_the_constructor()
    {
        var profile = new MemberProfile("Mia", "fox");

        profile.Change("Ola", "owl");

        Assert.Equal("Ola", profile.DisplayName);
        Assert.Equal("owl", profile.Avatar);
        Assert.Throws<ArgumentException>(() => profile.Change("", "owl"));
        Assert.Throws<ArgumentException>(() => profile.Change("Ola", "dragon"));
        Assert.Equal("Ola", profile.DisplayName); // unchanged after a refused change
    }

    [Fact]
    public void A_like_needs_a_profile_and_a_recipe()
    {
        Assert.Throws<ArgumentException>(() => new RecipeLike(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new RecipeLike(Guid.NewGuid(), Guid.Empty));
        var like = new RecipeLike(Guid.NewGuid(), Guid.NewGuid());
        Assert.NotEqual(Guid.Empty, like.MemberProfileId);
    }
}
