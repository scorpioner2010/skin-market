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

        if (item.HasIncomingDelivery)
        {
            return ResolveBuyerDeliveryAction(item.IncomingDeliveryStatus);
        }

        if (item.HasSellableItem)
        {
            return isTradeUrlConfigured
                ? new InventoryItemActionDecision { Kind = InventoryItemActionKinds.Sell, DiagnosticReason = "Tradable asset is available." }
                : new InventoryItemActionDecision { Kind = InventoryItemActionKinds.TradeUrlRequired, DiagnosticReason = "Tradable asset is available but seller Trade URL is missing." };
        }

        if (item.HasDeliveredPurchase)
        {
            return new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.Delivered,
                Status = "Delivered",
                DiagnosticReason = "Delivered buyer purchase fallback is visible."
            };
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
            DiagnosticReason = "No sellable asset, seller trade operation, buyer delivery, delivered purchase, or trade protection state matched."
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

    private static InventoryItemActionDecision ResolveBuyerDeliveryAction(string? status)
    {
        return status switch
        {
            "PendingDelivery" or "DeliveryBotPending" or "DeliveryInEscrow" => new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.DeliveryPending,
                Status = status,
                DiagnosticReason = "Buyer delivery is active."
            },
            "AwaitingBotConfirmation" => new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.AwaitingBotConfirmation,
                Status = status,
                DiagnosticReason = "Delivery offer is waiting for bot confirmation."
            },
            "DeliveryTradeCreated" or "AwaitingBuyerAction" => new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.AwaitingBuyerAcceptance,
                Status = status,
                TradeOfferId = null,
                DiagnosticReason = "Buyer delivery offer is waiting for buyer action."
            },
            _ => new InventoryItemActionDecision
            {
                Kind = InventoryItemActionKinds.UnknownState,
                Status = status,
                DiagnosticReason = $"Unhandled buyer delivery status '{status ?? "<null>"}'."
            }
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
