using Microsoft.Extensions.Logging.Abstractions;
using SkinMarket.Contracts;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Tests;

public class PricingSourceSemanticsTests
{
    [Theory]
    [InlineData("$1.23", "1.23")]
    [InlineData("$1,234.56", "1234.56")]
    [InlineData("1.234,56€", "1234.56")]
    [InlineData("$1,234", "1234")]
    [InlineData("1,23â‚¬", "1.23")]
    public void SteamPriceParser_UsesDecimalAndLocalizedSeparators(string raw, string expected)
    {
        Assert.Equal(decimal.Parse(expected), SteamMarketPriceService.ParsePrice(raw));
    }

    [Fact]
    public void SkinportLivePrice_UsesMinPriceOnlyWhenQuantityExists()
    {
        var item = new SkinportItemDto
        {
            MinPrice = 12.34m,
            SuggestedPrice = 99.99m,
            Quantity = 3
        };

        Assert.Equal(12.34m, SkinportPricingService.TryGetLiveMinPriceUsd(item));

        item.Quantity = 0;
        Assert.Null(SkinportPricingService.TryGetLiveMinPriceUsd(item));
    }

    [Fact]
    public void SkinportOutOfStock_UsesAvgSaleBeforeSuggested()
    {
        var item = new SkinportOutOfStockItemDto
        {
            AvgSalePrice = 8.75m,
            SuggestedPrice = 30m
        };

        Assert.Equal(8.75m, SkinportPricingService.TryGetOutOfStockEstimateUsd(item));
    }

    [Fact]
    public void SkinportLiveMap_UsesNormalizedKeysAndKeepsLowestInStockDuplicate()
    {
        var map = SkinportPricingService.BuildItemsPriceMap(
        [
            new SkinportItemDto { MarketHashName = "AK-47\u00A0|\u00A0Redline", MinPrice = 20m, Quantity = 2 },
            new SkinportItemDto { MarketHashName = "AK-47 | Redline", MinPrice = 12m, Quantity = 1 },
            new SkinportItemDto { MarketHashName = "AK-47 | Redline", MinPrice = 8m, Quantity = 0 }
        ]);

        var item = Assert.Single(map);
        Assert.Equal("AK-47 | Redline", item.Key);
        Assert.Equal(12m, item.Value.MinPrice);
        Assert.Equal(1, item.Value.Quantity);
    }

    [Fact]
    public void SkinportHistoryMap_PrefersUsableSevenDayMedianForDuplicate()
    {
        var map = SkinportPricingService.BuildSalesHistoryMap(
        [
            new SkinportSalesHistoryDto
            {
                MarketHashName = "AK-47 | Redline",
                Last30Days = new SkinportSalesWindowDto { Median = 15m, Volume = 2 }
            },
            new SkinportSalesHistoryDto
            {
                MarketHashName = "AK-47\u00A0|\u00A0Redline",
                Last7Days = new SkinportSalesWindowDto { Median = 13m, Volume = 1 }
            }
        ]);

        Assert.Equal(13m, map["AK-47 | Redline"].Last7Days?.Median);
    }

    [Fact]
    public void CsFloat_UsesTopLevelListingPrice_NotScmReference()
    {
        var listing = new CsFloatListingDto
        {
            Price = 1234,
            Item = new CsFloatItemDto
            {
                Scm = new CsFloatScmDto { Price = 999999 }
            }
        };

        Assert.Equal(12.34m, CsFloatPriceService.TryGetTopLevelListingPriceUsd(listing));
    }

    [Fact]
    public void ItemPriceResolver_SourcePriority_IsSteamThenSkinportThenDMarket()
    {
        var orderedSources = new[]
            {
                Candidate(PriceSourceNames.DMarket, 1.00m, 1.00m),
                Candidate(PriceSourceNames.Skinport, 2.00m, 0.50m),
                Candidate(PriceSourceNames.Steam, 3.00m, 0.10m)
            }
            .OrderBy(ItemPriceResolver.GetSelectionRank)
            .ThenByDescending(item => item.ConfidenceScore)
            .ThenBy(item => item.Price)
            .Select(item => item.Source)
            .ToList();

        Assert.Equal(
            [PriceSourceNames.Steam, PriceSourceNames.Skinport, PriceSourceNames.DMarket],
            orderedSources);
    }

    [Fact]
    public void DMarketMoney_ParsesOfferBestPriceAsUsdDecimal()
    {
        var price = DMarketPricingService.ParseMoney(new DMarketMoneyDto
        {
            CurrencyUpper = "USD",
            AmountUpper = "15.50"
        });

        Assert.NotNull(price);
        Assert.Equal(15.50m, price.Value.Amount);
        Assert.Equal("USD", price.Value.Currency);
    }

    [Fact]
    public void DMarketMoney_ParsesIntegerAmountAsUsdMinorUnits()
    {
        var price = DMarketPricingService.ParseMoney(new DMarketMoneyDto
        {
            CurrencyUpper = "USD",
            AmountUpper = "1550"
        });

        Assert.NotNull(price);
        Assert.Equal(15.50m, price.Value.Amount);
        Assert.Equal("USD", price.Value.Currency);
    }

    [Fact]
    public void DMarketOfferBestPrice_IsPreferredOverSuggestedPrice()
    {
        var item = new DMarketAggregatedPriceDto
        {
            OfferBestPrice = Money("12.34"),
            SuggestedPrice = Money("99.99"),
            RecommendedPrice = Money("88.88")
        };

        Assert.Equal(12.34m, DMarketPricingService.TryGetOfferBestPriceUsd(item));
        Assert.Equal(99.99m, DMarketPricingService.TryGetSuggestedPriceUsd(item));
    }

    [Fact]
    public void DMarketSuggestedPrice_IsEstimateOnlyWhenNoOfferExists()
    {
        var item = new DMarketAggregatedPriceDto
        {
            SuggestedPrice = Money("9.87")
        };

        Assert.Null(DMarketPricingService.TryGetOfferBestPriceUsd(item));
        Assert.Equal(9.87m, DMarketPricingService.TryGetSuggestedPriceUsd(item));
    }

    [Fact]
    public void MarketPricingService_DoesNotReturnFakeFallbackWhenPriceMissing()
    {
        var service = new MarketPricingService(
            new FakeItemPriceResolver(new ItemPriceResolutionResult
            {
                HasPrice = false,
                FailureReason = "No reliable price.",
                Source = PriceSourceNames.Unavailable,
                PriceType = PriceTypeNames.Unavailable
            }),
            new GameCatalog(),
            NullLogger<MarketPricingService>.Instance);

        var price = service.CalculatePrice(new SteamInventoryItemDto
        {
            GameType = GameType.CS2,
            Name = "AK-47 | Redline",
            MarketHashName = "AK-47 | Redline",
            AssetId = "asset-1",
            Tradable = true,
            Marketable = true
        });

        Assert.Null(price);
    }

    [Fact]
    public async Task MarketPricingService_UsesResolvedUsdPriceWhenAvailable()
    {
        var service = new MarketPricingService(
            new FakeItemPriceResolver(new ItemPriceResolutionResult
            {
                HasPrice = true,
                Price = 100m,
                Source = PriceSourceNames.Skinport,
                PriceType = PriceTypeNames.LowestListing
            }),
            new GameCatalog(),
            NullLogger<MarketPricingService>.Instance);

        var price = await service.CalculatePriceAsync(new SteamInventoryItemDto
        {
            GameType = GameType.CS2,
            MarketHashName = "AK-47 | Redline",
            AssetId = "asset-1"
        });

        Assert.Equal(100m, price);
    }

    [Fact]
    public async Task UsdOnlyFxService_RejectsNonUsdInsteadOfMislabelingCurrency()
    {
        var service = new UsdOnlyFxRateService();

        var usd = await service.NormalizeToUsdAsync(12.34m, "USD");
        var eur = await service.NormalizeToUsdAsync(12.34m, "EUR");

        Assert.True(usd.Success);
        Assert.Equal(12.34m, usd.PriceUsd);
        Assert.Equal(1m, usd.FxRate);
        Assert.False(eur.Success);
        Assert.Null(eur.PriceUsd);
        Assert.Contains("FX conversion is not configured", eur.FailureReason);
    }

    [Fact]
    public void UnavailableResult_DoesNotUseZeroPrice()
    {
        var result = new ItemPriceResolutionResult
        {
            HasPrice = false,
            Price = null,
            Source = PriceSourceNames.Unavailable,
            PriceType = PriceTypeNames.Unavailable,
            FailureReason = "No reliable price."
        };

        Assert.False(result.HasPrice);
        Assert.Null(result.DisplayPriceUsd);
    }

    private static DMarketMoneyDto Money(string amount)
    {
        return new DMarketMoneyDto
        {
            CurrencyUpper = "USD",
            AmountUpper = amount
        };
    }

    private static ItemPriceResolutionResult Candidate(string source, decimal price, decimal confidenceScore)
    {
        return new ItemPriceResolutionResult
        {
            HasPrice = true,
            Price = price,
            Currency = "USD",
            Source = source,
            PriceType = PriceTypeNames.LowestListing,
            ConfidenceScore = confidenceScore
        };
    }

    private sealed class FakeItemPriceResolver : IItemPriceResolver
    {
        private readonly ItemPriceResolutionResult _result;

        public FakeItemPriceResolver(ItemPriceResolutionResult result)
        {
            _result = result;
        }

        public Task<ItemPriceResolutionResult> ResolveAsync(SteamInventoryItemDto item, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }

        public Task<ItemPriceResolutionResult> ResolveAsync(TradeOperation operation, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }

        public Task<ItemPriceResolutionResult> ResolveAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }

        public Task<ItemPriceResolutionResult> GetCachedAsync(string marketHashName, GameType gameType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }

        public Task<Dictionary<string, ItemPriceResolutionResult>> GetCachedAsync(IReadOnlyCollection<string> marketHashNames, GameType gameType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(marketHashNames.ToDictionary(item => item, _ => _result, StringComparer.Ordinal));
        }

        public Task<Dictionary<string, ItemPriceResolutionResult>> ResolveInventoryPricesAsync(IReadOnlyCollection<SteamInventoryItemDto> items, GameType gameType, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, ItemPriceResolutionResult>(StringComparer.Ordinal));
        }
    }

}
