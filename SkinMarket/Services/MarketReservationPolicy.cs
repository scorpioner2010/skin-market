using SkinMarket.Models;

namespace SkinMarket.Services;

public static class MarketReservationPolicy
{
    public static readonly string[] ActiveReservationDeliveryStatuses =
    [
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow",
        "Delivered"
    ];

    private static readonly HashSet<string> ActiveDeliveryStatusSet = new(
        ActiveReservationDeliveryStatuses,
        StringComparer.Ordinal);

    public static MarketReservationDecision GetDecision(MarketPurchaseRecord purchase, bool assetCurrentlyInBotInventory)
    {
        if (string.Equals(purchase.Status, "Sold", StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(purchase.DeliveryStatus) ||
             ActiveDeliveryStatusSet.Contains(purchase.DeliveryStatus)))
        {
            if (string.Equals(purchase.DeliveryStatus, "Delivered", StringComparison.Ordinal))
            {
                return assetCurrentlyInBotInventory
                    ? new MarketReservationDecision(true, "Delivered item still appears in bot inventory", ShouldWarn: true)
                    : new MarketReservationDecision(true, "Delivered and asset is no longer in bot inventory");
            }

            return new MarketReservationDecision(true, purchase.DeliveryStatus ?? "Sold without delivery progress");
        }

        return new MarketReservationDecision(false, purchase.DeliveryStatus ?? purchase.Status);
    }
}

public sealed record MarketReservationDecision(bool IsReserved, string Reason, bool ShouldWarn = false);
