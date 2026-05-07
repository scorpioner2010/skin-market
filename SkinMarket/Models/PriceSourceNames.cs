namespace SkinMarket.Models;

public static class PriceSourceNames
{
    public const string Steam = "Steam";
    public const string Skinport = "Skinport";
    public const string CSFloat = "CSFloat";
    public const string DMarket = "DMarket";
    public const string BitSkins = "BitSkins";
    public const string Aggregator = "Aggregator";
    public const string Unavailable = "Unavailable";
}

public static class PriceTypeNames
{
    public const string LowestListing = "lowest_listing";
    public const string BestBid = "best_bid";
    public const string MedianSale = "median_sale";
    public const string AvgSale = "avg_sale";
    public const string Suggested = "suggested";
    public const string ReferenceExternal = "reference_external";
    public const string BlendedEstimate = "blended_estimate";
    public const string Unavailable = "unavailable";
}
