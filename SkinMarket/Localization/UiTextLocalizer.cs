using Microsoft.Extensions.Localization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SkinMarket.Localization;

public static partial class UiTextLocalizer
{
    public static string LocalizeStatus(IStringLocalizer localizer, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.Empty;
        }

        return Resolve(localizer, $"Status_{status}", status);
    }

    public static string LocalizeBalanceTransactionType(IStringLocalizer localizer, string? transactionType)
    {
        if (string.IsNullOrWhiteSpace(transactionType))
        {
            return string.Empty;
        }

        return Resolve(localizer, $"BalanceType_{transactionType}", transactionType);
    }

    public static string LocalizePriceSource(IStringLocalizer localizer, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        return Resolve(localizer, $"PriceSource_{source}", source);
    }

    public static string LocalizePriceStatus(IStringLocalizer localizer, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return string.Empty;
        }

        return Resolve(localizer, $"PriceStatus_{status}", status);
    }

    public static string LocalizeMessage(IStringLocalizer localizer, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var creditedMatch = CreditedMessageRegex().Match(message);
        if (creditedMatch.Success)
        {
            var amount = creditedMatch.Groups["amount"].Value;
            return localizer["Message_BalanceCreditedBy", amount];
        }

        return message switch
        {
            "Steam login is required to create a sale request." => Resolve(localizer, "Message_LoginRequiredSaleRequest", message),
            "Trade URL must be set before creating a sale request." => Resolve(localizer, "Message_TradeUrlRequiredSale", message),
            "Selected inventory item is invalid." => Resolve(localizer, "Message_SelectedItemInvalid", message),
            "This item already has a sale operation." => Resolve(localizer, "Message_ItemAlreadyHasSaleOperation", message),
            "Sale request created." => Resolve(localizer, "Message_SaleRequestCreated", message),
            "Steam login is required to create bot intake." => Resolve(localizer, "Message_LoginRequiredBotIntake", message),
            "Sale request is invalid." => Resolve(localizer, "Message_SaleRequestInvalid", message),
            "Steam login is required to credit balance." => Resolve(localizer, "Message_LoginRequiredCreditBalance", message),
            "Trade URL is not set yet. Inventory still loads by SteamID." => Resolve(localizer, "Message_TradeUrlNotSetInventoryStillLoads", message),
            "SteamID is not available for the current session." => Resolve(localizer, "Message_SteamIdUnavailable", message),
            "Local user profile was not found." => Resolve(localizer, "Message_LocalUserProfileNotFound", message),
            "Steam login is required to buy market items." => Resolve(localizer, "Message_LoginRequiredBuyMarketItems", message),
            "Steam login is required to create delivery trade." => Resolve(localizer, "Message_LoginRequiredCreateDeliveryTrade", message),
            "Steam login is required to confirm delivery." => Resolve(localizer, "Message_LoginRequiredConfirmDelivery", message),
            "Trade URL must be a valid Steam trade offer link." => Resolve(localizer, "Message_TradeUrlMustBeValid", message),
            "Trade URL saved." => Resolve(localizer, "Message_TradeUrlSaved", message),
            "Sale request was not found." => Resolve(localizer, "Message_SaleRequestNotFound", message),
            "This sale request was already credited." => Resolve(localizer, "Message_SaleRequestAlreadyCredited", message),
            "Only trade-created requests can be credited." => Resolve(localizer, "Message_OnlyTradeCreatedCanBeCredited", message),
            "Trade intake is available only for pending sale requests." => Resolve(localizer, "Message_TradeIntakeOnlyPending", message),
            "Bot integration is not fully configured yet. Complete bot settings to enable real trade creation." => Resolve(localizer, "Message_BotIntegrationNotConfigured", message),
            "Purchased item was not found." => Resolve(localizer, "Message_PurchasedItemNotFound", message),
            "Only sold items can enter delivery flow." => Resolve(localizer, "Message_OnlySoldItemsCanEnterDelivery", message),
            "Item was already delivered." => Resolve(localizer, "Message_ItemAlreadyDelivered", message),
            "Delivery trade already exists for this item." => Resolve(localizer, "Message_DeliveryTradeAlreadyExists", message),
            "Buyer profile was not found." => Resolve(localizer, "Message_BuyerProfileNotFound", message),
            "Buyer Trade URL is required before delivery can start." => Resolve(localizer, "Message_BuyerTradeUrlRequired", message),
            "Delivery bot integration is not fully configured yet. Complete bot settings to enable real delivery trade creation." => Resolve(localizer, "Message_DeliveryBotNotConfigured", message),
            "Item was already marked as delivered." => Resolve(localizer, "Message_ItemAlreadyMarkedDelivered", message),
            "Only delivery-created items can be confirmed as delivered." => Resolve(localizer, "Message_OnlyDeliveryCreatedCanBeConfirmed", message),
            "Item marked as delivered." => Resolve(localizer, "Message_ItemMarkedDelivered", message),
            "Market item was not found." => Resolve(localizer, "Message_MarketItemNotFound", message),
            "This item is no longer available." => Resolve(localizer, "Message_MarketItemNoLongerAvailable", message),
            "Buying your own market item is not allowed." => Resolve(localizer, "Message_BuyOwnItemNotAllowed", message),
            "Not enough balance to buy this item." => Resolve(localizer, "Message_NotEnoughBalance", message),
            "Purchase completed. Item is pending delivery." => Resolve(localizer, "Message_PurchaseCompletedPendingDelivery", message),
            "Steam is temporarily rate-limiting inventory requests. Please try again in a few minutes." => Resolve(localizer, "Message_SteamRateLimited", message),
            "Steam inventory response was empty or invalid." => Resolve(localizer, "Message_SteamInventoryInvalid", message),
            "Steam inventory is unavailable or private." => Resolve(localizer, "Message_SteamInventoryUnavailable", message),
            "Steam inventory response is incomplete: assets were not returned." => Resolve(localizer, "Message_SteamInventoryIncomplete", message),
            "Stub bot trade created. Real Steam bot integration is not connected yet." => Resolve(localizer, "Message_StubBotTradeCreated", message),
            "Stub delivery trade created. Real Steam delivery integration is not connected yet." => Resolve(localizer, "Message_StubDeliveryTradeCreated", message),
            _ => message
        };
    }

    private static string Resolve(IStringLocalizer localizer, string key, string fallback)
    {
        var localized = localizer[key];
        return localized.ResourceNotFound ? fallback : localized.Value;
    }

    [GeneratedRegex(@"^Balance credited by (?<amount>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CreditedMessageRegex();
}
