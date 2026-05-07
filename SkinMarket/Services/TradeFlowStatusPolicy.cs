using SkinMarket.Models;

namespace SkinMarket.Services;

public static class TradeFlowStatusPolicy
{
    public static readonly string[] ActiveIntakeStatuses =
    [
        "Pending",
        "BotPending",
        "AwaitingBotConfirmation",
        "TradeCreated",
        "AwaitingUserAction",
        "TradeAcceptedPendingReceipt",
        "ReceivedByBot",
        "InEscrow"
    ];

    public static readonly string[] ActiveDeliveryStatuses =
    [
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow"
    ];

    public static bool IsActiveIntakeStatus(string? status)
    {
        return status is not null && ActiveIntakeStatuses.Contains(status);
    }

    public static bool IsActiveDelivery(MarketPurchaseRecord purchase)
    {
        return IsActiveDeliveryStatus(purchase.DeliveryStatus, purchase.DeliveryTradeOfferId);
    }

    public static bool IsActiveDeliveryStatus(string? status, string? deliveryTradeOfferId)
    {
        if (status is null || !ActiveDeliveryStatuses.Contains(status))
        {
            return false;
        }

        return !string.Equals(status, "AwaitingBotConfirmation", StringComparison.Ordinal) ||
               !string.IsNullOrWhiteSpace(deliveryTradeOfferId);
    }
}
