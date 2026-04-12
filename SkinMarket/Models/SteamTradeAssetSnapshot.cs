namespace SkinMarket.Models;

public class SteamTradeAssetSnapshot
{
    public string AssetId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public int AppId { get; set; }
    public string ContextId { get; set; } = string.Empty;
}
