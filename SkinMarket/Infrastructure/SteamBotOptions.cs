namespace SkinMarket.Infrastructure;

public class SteamBotOptions
{
    public const string SectionName = "SteamBot";

    public bool Enabled { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string BotSteamId { get; set; } = string.Empty;
    public string BotTradeUrl { get; set; } = string.Empty;
    public string SharedSecret { get; set; } = string.Empty;
    public string IdentitySecret { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = "http://127.0.0.1:5174";
}
