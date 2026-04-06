namespace SkinMarket.Infrastructure;

public class SteamBotOptions
{
    public const string SectionName = "SteamBot";

    public bool Enabled { get; set; }
    public string BotSteamId { get; set; } = string.Empty;
    public string BotTradeUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
    public string IdentitySecret { get; set; } = string.Empty;
}
