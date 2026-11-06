using Foodplanner.Domain.ShoppingLists;

namespace Foodplanner.Api.Features.ShoppingLists;

/// <summary>
/// "This week" for the regular groceries (T-109). Households have no time zone of their own and the product is Norwegian, so
/// the current week is the ISO week of today in Europe/Oslo (UTC when the zone database is missing). A client far away from
/// Oslo can differ for a few hours around Sunday midnight; that only moves which week "this week" means on the household page.
/// </summary>
internal static class GroceryCalendar
{
    private static readonly TimeZoneInfo Zone = FindZone();

    public static DateOnly CurrentWeek(TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);
        return GrocerySchedule.WeekStartOf(DateOnly.FromDateTime(local.DateTime));
    }

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
