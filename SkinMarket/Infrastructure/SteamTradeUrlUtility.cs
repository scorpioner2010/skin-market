using Microsoft.AspNetCore.WebUtilities;

namespace SkinMarket.Infrastructure;

public static class SteamTradeUrlUtility
{
    public static bool IsValidTradeOfferUrl(string? tradeUrl)
    {
        return TryParseTradeOfferUrl(tradeUrl, requireToken: true, out _, out _);
    }

    public static bool BelongsToSteamId(string? tradeUrl, string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return false;
        }

        if (!TryParseTradeOfferUrl(tradeUrl, requireToken: true, out var partnerAccountId, out _))
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

    private static bool TryParseTradeOfferUrl(
        string? tradeUrl,
        bool requireToken,
        out uint partnerAccountId,
        out string? token)
    {
        partnerAccountId = 0;
        token = null;

        if (string.IsNullOrWhiteSpace(tradeUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(tradeUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals("/tradeoffer/new/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        var partnerValues = query
            .FirstOrDefault(entry => entry.Key.Equals("partner", StringComparison.OrdinalIgnoreCase))
            .Value;
        if (!uint.TryParse(partnerValues.ToString(), out partnerAccountId))
        {
            return false;
        }

        var tokenValues = query
            .FirstOrDefault(entry => entry.Key.Equals("token", StringComparison.OrdinalIgnoreCase))
            .Value;
        token = tokenValues.ToString();

        return !requireToken || !string.IsNullOrWhiteSpace(token);
    }
}
