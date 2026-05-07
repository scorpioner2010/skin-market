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
}
