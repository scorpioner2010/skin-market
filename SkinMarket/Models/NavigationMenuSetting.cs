namespace SkinMarket.Models;

public class NavigationMenuSetting
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public static class NavigationMenuKeys
{
    public const string Items = "items";
    public const string Games = "games";
    public const string Chats = "chats";
    public const string Market = "market";
    public const string Inventory = "inventory";
    public const string History = "history";

    public static readonly IReadOnlyList<NavigationMenuDefinition> EditableMenus =
    [
        new(Items, "Items", 10),
        new(Games, "Games", 20),
        new(Chats, "Chats", 30),
        new(Market, "Market", 40),
        new(Inventory, "Inventory", 50),
        new(History, "History", 60)
    ];
}

public sealed record NavigationMenuDefinition(string Key, string DisplayName, int SortOrder);
