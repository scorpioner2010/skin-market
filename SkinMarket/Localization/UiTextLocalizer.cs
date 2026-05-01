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

        var cannotCancelMatch = TradeOfferCannotBeCanceledRegex().Match(message);
        if (cannotCancelMatch.Success)
        {
            var status = cannotCancelMatch.Groups["status"].Value;
            return localizer["Message_TradeOfferCannotBeCanceledStatus", LocalizeStatus(localizer, status)];
        }

        var canceledBySellerMatch = TradeOfferCanceledBySellerRegex().Match(message);
        if (canceledBySellerMatch.Success)
        {
            var reason = LocalizeMessage(localizer, canceledBySellerMatch.Groups["message"].Value);
            return localizer["Message_TradeOfferCanceledBySeller", reason];
        }

        var steamStatusMatch = SteamStatusCheckedRegex().Match(message);
        if (steamStatusMatch.Success)
        {
            var steamState = steamStatusMatch.Groups["steamState"].Value;
            var appStatus = LocalizeStatus(localizer, steamStatusMatch.Groups["appStatus"].Value);
            var steamMessage = steamStatusMatch.Groups["message"].Value;
            return localizer["Message_SteamStatusChecked", steamState, appStatus, steamMessage];
        }

        var minimumBetMatch = MinimumBetRegex().Match(message);
        if (minimumBetMatch.Success)
        {
            return localizer["Message_MinefieldMinimumBet", minimumBetMatch.Groups["amount"].Value];
        }

        var maximumBetMatch = MaximumBetRegex().Match(message);
        if (maximumBetMatch.Success)
        {
            return localizer["Message_MinefieldMaximumBet", maximumBetMatch.Groups["amount"].Value];
        }

        var cachedInventoryMatch = CachedInventoryRegex().Match(message);
        if (cachedInventoryMatch.Success)
        {
            return localizer["Message_CachedInventoryShown", cachedInventoryMatch.Groups["time"].Value];
        }

        return message switch
        {
            "Database-backed features are temporarily unavailable." => Resolve(localizer, "Message_ServiceUnavailable", message),
            "Steam login is required to create a sale request." => Resolve(localizer, "Message_LoginRequiredSaleRequest", message),
            "Trade URL must be set before creating a sale request." => Resolve(localizer, "Message_TradeUrlRequiredSale", message),
            "Selected inventory item is invalid." => Resolve(localizer, "Message_SelectedItemInvalid", message),
            "This item already has a sale operation." => Resolve(localizer, "Message_ItemAlreadyHasSaleOperation", message),
            "Sale request created." => Resolve(localizer, "Message_SaleRequestCreated", message),
            "Sale request created. Intake trade will start automatically." => Resolve(localizer, "Message_SaleRequestCreatedAuto", message),
            "Finish or cancel the active trade offer before selling another item." => Resolve(localizer, "Message_FinishActiveTradeSell", message),
            "Finish or cancel the active trade offer before buying another item." => Resolve(localizer, "Message_FinishActiveTradeBuy", message),
            "Steam login is required to create bot intake." => Resolve(localizer, "Message_LoginRequiredBotIntake", message),
            "Sale request is invalid." => Resolve(localizer, "Message_SaleRequestInvalid", message),
            "Cancel request is invalid." => Resolve(localizer, "Message_CancelRequestInvalid", message),
            "Steam login is required to credit balance." => Resolve(localizer, "Message_LoginRequiredCreditBalance", message),
            "Trade URL is not set yet. Inventory still loads by SteamID." => Resolve(localizer, "Message_TradeUrlNotSetInventoryStillLoads", message),
            "SteamID is not available for the current session." => Resolve(localizer, "Message_SteamIdUnavailable", message),
            "Local user profile was not found." => Resolve(localizer, "Message_LocalUserProfileNotFound", message),
            "Trade URL belongs to another Steam account." => Resolve(localizer, "Message_TradeUrlBelongsAnotherSteam", message),
            "Gift code is invalid." => Resolve(localizer, "Message_GiftCodeInvalid", message),
            "Gift code activated." => Resolve(localizer, "Message_GiftCodeActivated", message),
            "Item was not found." => Resolve(localizer, "Message_ItemNotFound", message),
            "Message is invalid." => Resolve(localizer, "Message_MessageInvalid", message),
            "Message cannot be empty." => Resolve(localizer, "Message_MessageEmpty", message),
            "Chat was not found." => Resolve(localizer, "Message_ChatNotFound", message),
            "Admin account was not found." => Resolve(localizer, "Message_AdminAccountNotFound", message),
            "Steam login is required to buy market items." => Resolve(localizer, "Message_LoginRequiredBuyMarketItems", message),
            "Steam login is required to create delivery trade." => Resolve(localizer, "Message_LoginRequiredCreateDeliveryTrade", message),
            "Steam login is required to confirm delivery." => Resolve(localizer, "Message_LoginRequiredConfirmDelivery", message),
            "Trade URL must be a valid Steam trade offer link." => Resolve(localizer, "Message_TradeUrlMustBeValid", message),
            "Trade URL saved." => Resolve(localizer, "Message_TradeUrlSaved", message),
            "Sale request was not found." => Resolve(localizer, "Message_SaleRequestNotFound", message),
            "This sale request does not have a Steam trade offer yet." => Resolve(localizer, "Message_TradeOfferNoSteamYet", message),
            "Could not check Steam offer status. Bot service did not return a status." => Resolve(localizer, "Message_CheckSteamOfferStatusFailed", message),
            "Steam still reports this offer as active. If you clicked Confirm, wait a few seconds and check again; otherwise complete the Steam confirmation popup or Steam mobile confirmation." => Resolve(localizer, "Message_SteamOfferStillActive", message),
            "Trade offer was canceled." => Resolve(localizer, "Message_TradeOfferCanceled", message),
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
            "Not enough balance." => Resolve(localizer, "Message_NotEnoughBalanceGeneric", message),
            "Minefield is currently disabled." => Resolve(localizer, "Message_MinefieldDisabled", message),
            "Selected cell is invalid." => Resolve(localizer, "Message_SelectedCellInvalid", message),
            "Active game was not found." => Resolve(localizer, "Message_ActiveGameNotFound", message),
            "Selected row is not active." => Resolve(localizer, "Message_SelectedRowInactive", message),
            "Open at least one safe row before claiming." => Resolve(localizer, "Message_OpenSafeRowBeforeClaim", message),
            "Claim step is invalid." => Resolve(localizer, "Message_ClaimStepInvalid", message),
            "Cannot claim after a mine step." => Resolve(localizer, "Message_CannotClaimAfterMine", message),
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

    [GeneratedRegex(@"^Trade offer cannot be canceled from status (?<status>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex TradeOfferCannotBeCanceledRegex();

    [GeneratedRegex(@"^Trade offer was canceled by seller\. (?<message>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TradeOfferCanceledBySellerRegex();

    [GeneratedRegex(@"^Steam status checked\. SteamState=(?<steamState>[^;]+); AppStatus=(?<appStatus>[^;]+); Message=(?<message>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamStatusCheckedRegex();

    [GeneratedRegex(@"^Minimum bet is (?<amount>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex MinimumBetRegex();

    [GeneratedRegex(@"^Maximum bet is (?<amount>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex MaximumBetRegex();

    [GeneratedRegex(@"^Steam is rate-limiting live inventory requests, so cached inventory from (?<time>.+) is shown\.$", RegexOptions.CultureInvariant)]
    private static partial Regex CachedInventoryRegex();
}
