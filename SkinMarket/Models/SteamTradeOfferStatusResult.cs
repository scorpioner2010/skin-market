namespace SkinMarket.Models;

public class SteamTradeOfferStatusResult
{
    public string OfferId { get; set; } = string.Empty;
    public string Flow { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public string State { get; set; } = string.Empty;
    public string RawState { get; set; } = string.Empty;
    public bool IsTerminal { get; set; }
    public bool IsSuccess { get; set; }
    public string? Message { get; set; }
    public SteamTradeAssetSnapshot? ReceivedItem { get; set; }
}
