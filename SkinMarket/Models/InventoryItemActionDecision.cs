namespace SkinMarket.Models;

public static class InventoryItemActionKinds
{
    public const string Sell = "Sell";
    public const string TradeUrlRequired = "TradeUrlRequired";
    public const string CreatingTradeOffer = "CreatingTradeOffer";
    public const string AwaitingSellerAcceptance = "AwaitingSellerAcceptance";
    public const string AwaitingBotConfirmation = "AwaitingBotConfirmation";
    public const string WaitingForCredit = "WaitingForCredit";
    public const string DeliveryPending = "DeliveryPending";
    public const string AwaitingBuyerAcceptance = "AwaitingBuyerAcceptance";
    public const string Delivered = "Delivered";
    public const string TradeProtected = "TradeProtected";
    public const string FailedRetry = "FailedRetry";
    public const string UnknownState = "UnknownState";
}

public sealed class InventoryItemActionDecision
{
    public string Kind { get; init; } = InventoryItemActionKinds.UnknownState;
    public string? Status { get; init; }
    public string? TradeOfferId { get; init; }
    public Guid? TradeOperationId { get; init; }
    public string DiagnosticReason { get; init; } = string.Empty;
    public bool IsUnknown => string.Equals(Kind, InventoryItemActionKinds.UnknownState, StringComparison.Ordinal);
}
