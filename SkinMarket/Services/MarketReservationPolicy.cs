using SkinMarket.Models;

namespace SkinMarket.Services;

public static class MarketReservationPolicy
{
    private static readonly HashSet<string> ActiveDeliveryStatuses = new(StringComparer.Ordinal)
    {
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow"
    };

    public static MarketReservationDecision GetDecision(MarketPurchaseRecord purchase, bool assetCurrentlyInBotInventory)
    {
        if (string.Equals(purchase.Status, "Sold", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(purchase.DeliveryStatus) ||
             ActiveDeliveryStatuses.Contains(purchase.DeliveryStatus)))
        {
            return new MarketReservationDecision(true, purchase.DeliveryStatus ?? "Sold without delivery progress");
        }

        if (string.Equals(purchase.DeliveryStatus, "Delivered", StringComparison.Ordinal))
        {
            return assetCurrentlyInBotInventory
                ? new MarketReservationDecision(false, "Delivered but asset is still in bot inventory")
                : new MarketReservationDecision(true, "Delivered and asset is no longer in bot inventory");
        }

        return new MarketReservationDecision(false, purchase.DeliveryStatus ?? purchase.Status);
    }
}

public sealed record MarketReservationDecision(bool IsReserved, string Reason);
