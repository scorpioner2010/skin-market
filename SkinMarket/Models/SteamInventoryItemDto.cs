namespace SkinMarket.Models;

public class SteamInventoryItemDto
{
    public GameType GameType { get; set; } = GameType.CS2;
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? MarketHashName { get; set; }
    public string? MarketName { get; set; }
    public string? IconUrl { get; set; }
    public bool? Tradable { get; set; }
    public bool? Marketable { get; set; }
}
