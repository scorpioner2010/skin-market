namespace SkinMarket.Models;

public sealed class GameDefinition
{
    public required GameType Type { get; init; }
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string ShortName { get; init; }
    public required int SteamAppId { get; init; }
    public required int SteamContextId { get; init; }
    public bool SupportsInventory { get; init; }
    public bool SupportsSteamMarketPricing { get; init; }
    public bool SupportsSkinportPricing { get; init; }
}
