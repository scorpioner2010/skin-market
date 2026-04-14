using SkinMarket.Models;

namespace SkinMarket.Services;

public static class ItemGroupingKeyUtility
{
    public static string ForInventory(SteamInventoryItemDto item)
    {
        return Build(
            MarketHashNameUtility.ResolvePrimary(item),
            item.Name,
            item.ClassId,
            item.InstanceId,
            item.Tradable,
            item.Marketable);
    }

    public static string ForMarket(MarketListingItem item)
    {
        return Build(
            item.MarketHashName,
            item.ItemName,
            item.ClassId,
            item.InstanceId,
            item.Tradable,
            item.Marketable);
    }

    private static string Build(
        string? marketHashName,
        string itemName,
        string classId,
        string instanceId,
        bool? tradable,
        bool? marketable)
    {
        var normalizedMarketHashName = MarketHashNameUtility.Normalize(marketHashName);
        var normalizedItemName = Normalize(itemName);
        var normalizedClassId = Normalize(classId);
        var normalizedInstanceId = Normalize(instanceId);
        var normalizedTradable = tradable.HasValue ? tradable.Value.ToString() : "null";
        var normalizedMarketable = marketable.HasValue ? marketable.Value.ToString() : "null";

        return string.Join(
            "::",
            normalizedMarketHashName ?? normalizedItemName,
            normalizedClassId,
            normalizedInstanceId,
            normalizedTradable,
            normalizedMarketable);
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }
}
