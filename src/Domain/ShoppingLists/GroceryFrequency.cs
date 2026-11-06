using System.Text.Json.Serialization;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>How often a regular grocery is due (T-109, Q-030). Stored by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroceryFrequency
{
    /// <summary>Every week. The default, and what every row from before T-109 is.</summary>
    Weekly = 0,

    /// <summary>Every second week, counted from the anchor week (<see cref="WeeklyGrocery.StartWeek"/>).</summary>
    EveryTwoWeeks = 1,

    /// <summary>Once a month, in the first week whose Monday falls in that month.</summary>
    Monthly = 2,
}
