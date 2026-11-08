using Foodplanner.Domain.Tenancy;

namespace Foodplanner.Domain.Tests.Tenancy;

public class AccountDeletionPlanTests
{
    private static AccountHouseholdFacts House(string name, HouseholdRole role, int members, int otherOwners = 0) =>
        new(Guid.NewGuid(), name, role, members, otherOwners);

    [Fact]
    public void The_only_member_deletes_the_household_with_the_account()
    {
        var solo = House("Solo", HouseholdRole.Owner, 1);
        var plan = AccountDeletionPlan.Create([solo]);
        Assert.Equal([solo], plan.ToDelete);
        Assert.Empty(plan.Blocking);
        Assert.Empty(plan.Remaining);
        Assert.True(plan.CanDelete);
    }

    [Fact]
    public void The_only_owner_with_other_members_blocks()
    {
        var family = House("Family", HouseholdRole.Owner, 3);
        var plan = AccountDeletionPlan.Create([family]);
        Assert.Equal([family], plan.Blocking);
        Assert.False(plan.CanDelete);
    }

    [Theory]
    [InlineData(HouseholdRole.Owner, 2, 1)]
    [InlineData(HouseholdRole.Adult, 2, 0)]
    [InlineData(HouseholdRole.Child, 5, 1)]
    public void Households_with_another_owner_or_where_the_user_is_no_owner_live_on(HouseholdRole role, int members, int otherOwners)
    {
        var home = House("Home", role, members, otherOwners);
        var plan = AccountDeletionPlan.Create([home]);
        Assert.Equal([home], plan.Remaining);
        Assert.True(plan.CanDelete);
    }

    [Fact]
    public void One_blocking_household_blocks_the_whole_deletion_but_the_others_are_still_classified()
    {
        var solo = House("Solo", HouseholdRole.Owner, 1);
        var family = House("Family", HouseholdRole.Owner, 2);
        var guest = House("Guest", HouseholdRole.Adult, 4, 1);
        var plan = AccountDeletionPlan.Create([solo, family, guest]);
        Assert.False(plan.CanDelete);
        Assert.Equal([solo], plan.ToDelete);
        Assert.Equal([family], plan.Blocking);
        Assert.Equal([guest], plan.Remaining);
    }

    [Fact]
    public void Groups_are_ordered_by_name_then_id()
    {
        var b = House("b", HouseholdRole.Owner, 1);
        var a = House("A", HouseholdRole.Owner, 1);
        var c = House("c", HouseholdRole.Owner, 1);
        Assert.Equal(["A", "b", "c"], AccountDeletionPlan.Create([c, b, a]).ToDelete.Select(h => h.Name));
    }

    [Fact]
    public void No_households_can_be_deleted()
    {
        var plan = AccountDeletionPlan.Create([]);
        Assert.True(plan.CanDelete);
        Assert.Empty(plan.ToDelete);
    }

    [Fact]
    public void The_confirmed_households_must_be_exactly_the_ones_to_delete()
    {
        var one = House("One", HouseholdRole.Owner, 1);
        var two = House("Two", HouseholdRole.Owner, 1);
        var plan = AccountDeletionPlan.Create([one, two]);
        Assert.True(plan.ToDeleteIs([two.HouseholdId, one.HouseholdId]));
        Assert.True(plan.ToDeleteIs([two.HouseholdId, one.HouseholdId, one.HouseholdId]));
        Assert.False(plan.ToDeleteIs([one.HouseholdId]));
        Assert.False(plan.ToDeleteIs([]));
        Assert.False(plan.ToDeleteIs([one.HouseholdId, two.HouseholdId, Guid.NewGuid()]));
        Assert.True(AccountDeletionPlan.Create([]).ToDeleteIs([]));
    }

    [Theory]
    [InlineData("a@example.com", "a@example.com", true)]
    [InlineData("  A@Example.COM ", "a@example.com", true)]
    [InlineData("b@example.com", "a@example.com", false)]
    [InlineData("", "a@example.com", false)]
    [InlineData("   ", "a@example.com", false)]
    [InlineData(null, "a@example.com", false)]
    [InlineData("delete", "a@example.com", false)]
    public void The_typed_confirmation_is_the_email_address(string? typed, string email, bool expected) =>
        Assert.Equal(expected, AccountDeletionConfirmation.Matches(typed, email));
}
