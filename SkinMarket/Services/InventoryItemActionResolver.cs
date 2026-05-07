using SkinMarket.Models;

namespace SkinMarket.Services;

public static class InventoryItemActionResolver
{
    public static InventoryItemActionDecision Resolve(GroupedInventoryItem item, bool isTradeUrlConfigured)
    {
        if (!string.IsNullOrWhiteSpace(item.ActiveTradeStatus))
        {
            return ResolveSellerTradeAction(item);
        }

        if (item.HasSellableItem)
        {
            return isTradeUrlConfigured
                ? new InventoryItemActionDecision { Kind = InventoryItemActionKinds.Sell, DiagnosticReason = "Tradable asset is available." }
                : new InventoryItemActionDecision { Kind = InventoryItemActionKinds.TradeUrlRequired, DiagnosticReason = "Tradable asset is available but seller Trade URL is missing." };
        }

        if (item.HasTradeProtected)
        {
            return new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.TradeProtected,
                Status = "TradeProtected",
                DiagnosticReason = "Inventory asset is visible but not tradable."
            };
        }

        return new InventoryItemActionDecision
        {
            Kind = InventoryItemActionKinds.UnknownState,
            DiagnosticReason = "No sellable asset, seller trade operation, or trade protection state matched."
        };
    }

    private static InventoryItemActionDecision ResolveSellerTradeAction(GroupedInventoryItem item)
    {
        return item.ActiveTradeStatus switch
        {
            "Pending" or "BotPending" => Build(item, InventoryItemActionKinds.CreatingTradeOffer),
            "AwaitingBotConfirmation" => Build(item, InventoryItemActionKinds.AwaitingBotConfirmation),
            "TradeCreated" or "AwaitingUserAction" => Build(item, InventoryItemActionKinds.AwaitingSellerAcceptance),
            "TradeAcceptedPendingReceipt" or "ReceivedByBot" or "InEscrow" => Build(item, InventoryItemActionKinds.WaitingForCredit),
            "Failed" => Build(item, InventoryItemActionKinds.FailedRetry),
            _ => Build(item, InventoryItemActionKinds.UnknownState, $"Unhandled seller trade status '{item.ActiveTradeStatus}'.")
        };
    }

    private static InventoryItemActionDecision Build(
        GroupedInventoryItem item,
        string kind,
        string? diagnosticReason = null)
    {
        return new InventoryItemActionDecision
        {
            Kind = kind,
            Status = item.ActiveTradeStatus,
            TradeOfferId = item.ActiveTradeOfferId,
            TradeOperationId = item.ActiveTradeOperationId,
            DiagnosticReason = diagnosticReason ?? "Seller trade operation controls this inventory row."
        };
    }
}
