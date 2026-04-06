namespace SkinMarket.Models;

public class ResolvedItemPrice
{
    public decimal? RealPriceUsd { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsFallback { get; set; }
}
