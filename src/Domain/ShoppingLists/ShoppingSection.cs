using System.Text.Json.Serialization;

namespace Foodplanner.Domain.ShoppingLists;

/// <summary>Where an item shows: the things to buy, or the staples the person only checks they have.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShoppingSection
{
    Main = 0,
    CheckYouHave = 1,
}
