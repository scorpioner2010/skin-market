namespace SkinMarket.Infrastructure;

public static class SteamConfigurationResolver
{
    public static SteamBotOptions ResolveSteamBotOptions(IConfiguration configuration)
    {
        var options = configuration.GetSection(SteamBotOptions.SectionName).Get<SteamBotOptions>() ?? new SteamBotOptions();

        options.Enabled = ResolveBoolean(configuration, options.Enabled, "STEAM_BOT_ENABLED", "SteamBot__Enabled");
        options.Username = ResolveString(configuration, options.Username, "STEAM_BOT_USERNAME", "SteamBot__Username");
        options.Password = ResolveString(configuration, options.Password, "STEAM_BOT_PASSWORD", "SteamBot__Password");
        options.BotSteamId = ResolveString(configuration, options.BotSteamId, "STEAM_BOT_STEAM_ID", "SteamBot__BotSteamId");
        options.BotTradeUrl = ResolveString(configuration, options.BotTradeUrl, "STEAM_BOT_TRADE_URL", "SteamBot__BotTradeUrl");
        options.SharedSecret = ResolveString(configuration, options.SharedSecret, "STEAM_BOT_SHARED_SECRET", "SteamBot__SharedSecret");
        options.IdentitySecret = ResolveString(configuration, options.IdentitySecret, "STEAM_BOT_IDENTITY_SECRET", "SteamBot__IdentitySecret");
        options.ServiceUrl = ResolveString(configuration, options.ServiceUrl, "STEAM_BOT_SERVICE_URL", "SteamBot__ServiceUrl");

        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            options.ServiceUrl = "http://127.0.0.1:5174";
        }

        return options;
    }

    public static SteamApiOptions ResolveSteamApiOptions(IConfiguration configuration)
    {
        var options = configuration.GetSection(SteamApiOptions.SectionName).Get<SteamApiOptions>() ?? new SteamApiOptions();
        options.ApiKey = ResolveString(configuration, options.ApiKey, "STEAM_API_KEY", "SteamApi__ApiKey");
        return options;
    }

    private static bool ResolveBoolean(IConfiguration configuration, bool currentValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (bool.TryParse(configuration[key], out var parsed))
            {
                return parsed;
            }
        }

        return currentValue;
    }

    private static string ResolveString(IConfiguration configuration, string currentValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return currentValue ?? string.Empty;
    }
}
