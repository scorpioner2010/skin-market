using Microsoft.AspNetCore.WebUtilities;

namespace SkinMarket.Infrastructure;

public static class SteamTradeUrlUtility
{
    public static bool BelongsToSteamId(string? tradeUrl, string? steamId)
    {
        if (string.IsNullOrWhiteSpace(tradeUrl) || string.IsNullOrWhiteSpace(steamId))
        {
            return false;
        }

        if (!Uri.TryCreate(tradeUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("partner", out var partnerValues))
        {
            return false;
        }

        if (!uint.TryParse(partnerValues.ToString(), out var partnerAccountId))
        {
            return false;
        }

        if (!ulong.TryParse(steamId.Trim(), out var steamId64))
        {
            return false;
        }

        const ulong steamId64Base = 76561197960265728;
        if (steamId64 <= steamId64Base)
        {
            return false;
        }

        var accountId = steamId64 - steamId64Base;
        return accountId == partnerAccountId;
    }
}
