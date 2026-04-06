using SkinMarket.Models;

namespace SkinMarket.Services;

public static class MarketHashNameUtility
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            ' ',
            value.Trim()
                .Replace('\u00A0', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string? ResolvePrimary(SteamInventoryItemDto item)
    {
        return Normalize(item.MarketHashName) ??
               Normalize(item.MarketName) ??
               Normalize(item.Name);
    }

    public static string? ResolvePrimary(TradeOperation operation)
    {
        return Normalize(operation.MarketHashName) ??
               Normalize(operation.ItemName);
    }
}
