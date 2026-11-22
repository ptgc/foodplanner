using System.Text.Json.Serialization;

namespace Foodplanner.Domain.Support;

/// <summary>Read sessions only look; write sessions may also apply the four named fixes (T-096).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportSessionMode
{
    Read = 1,
    Write = 2,
}

/// <summary>Why support looks at a household. A closed set: the household sees the category, never the free-text note.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportReasonCategory
{
    LostOwner = 1,
    StuckInvite = 2,
    PinLockout = 3,
    LockedWeek = 4,
    DataProblem = 5,
    Other = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupportAuditOutcome
{
    Success = 1,
    Denied = 2,
    Failed = 3,
}

/// <summary>The closed set of audited actions (T-096). Strings, because they are stored and shown.</summary>
public static class SupportActions
{
    public const string SessionStart = "session.start";
    public const string SessionEnd = "session.end";
    public const string SessionView = "session.view";
    public const string SearchHouseholds = "search.households";
    public const string SearchUsers = "search.users";
    public const string ViewOverview = "view.overview";
    public const string ViewMembers = "view.members";
    public const string ViewInvites = "view.invites";
    public const string ViewProfiles = "view.profiles";
    public const string ViewMealPlan = "view.meal-plan";
    public const string ViewShoppingList = "view.shopping-list";
    public const string ViewRecipes = "view.recipes";
    public const string ViewAccessLog = "view.access-log";
    public const string ViewPlan = "view.plan";
    public const string PlanSet = "plan.set";
    public const string PlanReset = "plan.reset";
    public const string InviteCancel = "invite.cancel";
    public const string MemberPromoteOwner = "member.promote-owner";
    public const string MemberPinUnlock = "member.pin-unlock";
    public const string WeekUnlock = "week.unlock";
    public const string AccessDenied = "access.denied";
}

/// <summary>How a support session ended. A session that ran out is simply read as ended from its expiry; nothing writes that.</summary>
public static class SupportEndedReasons
{
    public const string Ended = "ended";
    public const string Replaced = "replaced";
}
