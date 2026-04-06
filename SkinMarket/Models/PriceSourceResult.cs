namespace SkinMarket.Models;

public class PriceSourceResult
{
    public bool Success { get; set; }
    public decimal? Price { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
