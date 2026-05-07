using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Models;
using SkinMarket.Pages;
using SkinMarket.Services;

namespace SkinMarket.Tests;

public class TradeStateLogicTests
{
    [Fact]
    public void SellerPendingOperation_ShowsGlobalPanelStateWithoutTradeOffer()
    {
        var operation = Operation("asset-1", "Pending", tradeOfferId: null);
        var groups = BuildGroups([Item("asset-1")], [operation]);

        var action = Assert.Single(groups).ActionDecision;
        Assert.Equal(InventoryItemActionKinds.CreatingTradeOffer, action.Kind);
        Assert.Equal("Pending", action.Status);
        Assert.Null(action.TradeOfferId);
        Assert.Equal("Waiting for bot to create Steam offer", global::SaleStatusApiText.DescribeStatus("intake", "Pending", null));
        Assert.Equal("https://steamcommunity.com/my/tradeoffers/", global::SaleStatusApiText.BuildSteamOfferUrl(null));
    }

    [Fact]
    public void BotPendingOperation_StillShowsCreatingOfferAction()
    {
        var operation = Operation("asset-1", "BotPending");
        var group = Assert.Single(BuildGroups([Item("asset-1")], [operation]));

        Assert.Equal(InventoryItemActionKinds.CreatingTradeOffer, group.ActionDecision.Kind);
        Assert.Equal("BotPending", group.ActionDecision.Status);
    }

    [Fact]
    public void AwaitingUserActionWithOffer_ShowsSellerAcceptanceAction()
    {
        var operation = Operation("asset-1", "AwaitingUserAction", "123456");
        var group = Assert.Single(BuildGroups([Item("asset-1")], [operation]));

        Assert.Equal(InventoryItemActionKinds.AwaitingSellerAcceptance, group.ActionDecision.Kind);
        Assert.Equal("123456", group.ActionDecision.TradeOfferId);
        Assert.Equal("https://steamcommunity.com/tradeoffer/123456/", global::SaleStatusApiText.BuildSteamOfferUrl("123456"));
    }

    [Fact]
    public void ReceivedByBotSale_ShowsWaitingForCredit()
    {
        var operation = Operation("asset-1", "ReceivedByBot", "123456");
        var group = Assert.Single(BuildGroups([Item("asset-1")], [operation]));

        Assert.Equal(InventoryItemActionKinds.WaitingForCredit, group.ActionDecision.Kind);
    }

    [Fact]
    public void CreditedSellerSale_RemovesStaleInventoryItemFromPendingSaleDisplay()
    {
        var operation = Operation("asset-1", "Credited");
        var groups = BuildGroups([Item("asset-1")], [operation]);

        Assert.Empty(groups);
    }

    [Fact]
    public void DeliveredPurchaseWithoutSteamSnapshotItem_DoesNotCreateInventoryRow()
    {
        var purchase = Purchase("delivered:purchase-1", "Delivered");
        var groups = BuildGroups([], [], [purchase]);

        Assert.Empty(groups);
    }

    [Fact]
    public void DeliveredPurchaseWithMatchingTradableSteamSnapshotItem_CanBeSold()
    {
        var purchase = Purchase("asset-1", "Delivered");
        var group = Assert.Single(BuildGroups([Item("asset-1")], [], [purchase]));

        Assert.Equal(InventoryItemActionKinds.Sell, group.ActionDecision.Kind);
        Assert.Equal("asset-1", group.SellAssetId);
        Assert.DoesNotContain(group.StatusItems, item => string.Equals(item.Status, "Delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void DeliveredPurchaseWithMatchingTradeLockedSteamSnapshotItem_IsTradeProtected()
    {
        var purchase = Purchase("asset-1", "Delivered");
        var group = Assert.Single(BuildGroups([Item("asset-1", tradable: false)], [], [purchase]));

        Assert.Equal(InventoryItemActionKinds.TradeProtected, group.ActionDecision.Kind);
        Assert.Contains(group.StatusItems, item => item.Status == "TradeProtected" && item.Quantity == 1);
        Assert.DoesNotContain(group.StatusItems, item => string.Equals(item.Status, "Delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void DeliveryFailed_DoesNotReserveMarketAssetForever()
    {
        var failed = Purchase("asset-1", "DeliveryFailed");
        var deliveredStillInBotInventory = Purchase("asset-2", "Delivered");
        var active = Purchase("asset-3", "PendingDelivery");

        Assert.False(MarketReservationPolicy.GetDecision(failed, assetCurrentlyInBotInventory: true).IsReserved);
        var deliveredDecision = MarketReservationPolicy.GetDecision(deliveredStillInBotInventory, assetCurrentlyInBotInventory: true);
        Assert.True(deliveredDecision.IsReserved);
        Assert.True(deliveredDecision.ShouldWarn);
        Assert.Equal("Delivered item still appears in bot inventory", deliveredDecision.Reason);
        Assert.True(MarketReservationPolicy.GetDecision(active, assetCurrentlyInBotInventory: true).IsReserved);
    }

    [Fact]
    public void GroupedIdenticalItemsWithMixedStates_ShowAccurateCountsAndSellAction()
    {
        var items = new[]
        {
            Item("asset-ready"),
            Item("asset-pending"),
            Item("delivered:purchase-1", tradable: false)
        };
        var operations = new[] { Operation("asset-pending", "Pending") };
        var purchases = new[] { Purchase("delivered:purchase-1", "Delivered") };

        var groups = BuildGroups(items, operations, purchases);
        var group = Assert.Single(groups, item => item.AssetIds.Contains("asset-ready"));
        var tradeProtectedGroup = Assert.Single(groups, item => item.AssetIds.Contains("delivered:purchase-1"));

        Assert.Equal(InventoryItemActionKinds.CreatingTradeOffer, group.ActionDecision.Kind);
        Assert.Contains(group.StatusItems, item => item.IsReady && item.Quantity == 1);
        Assert.Contains(group.StatusItems, item => item.Status == "Pending" && item.Quantity == 1);
        Assert.Equal(InventoryItemActionKinds.TradeProtected, tradeProtectedGroup.ActionDecision.Kind);
        Assert.Contains(tradeProtectedGroup.StatusItems, item => item.Status == "TradeProtected" && item.Quantity == 1);
        Assert.DoesNotContain(groups, item => string.Equals(item.ActionDecision.Kind, "Delivered", StringComparison.Ordinal));
        Assert.DoesNotContain(groups.SelectMany(item => item.StatusItems), item => string.Equals(item.Status, "Delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void InventoryRows_NeverUseBuyerDeliveryActionKinds()
    {
        var purchases = new[]
        {
            Purchase("asset-1", "PendingDelivery"),
            Purchase("asset-2", "AwaitingBuyerAction"),
            Purchase("asset-3", "Delivered")
        };
        var groups = BuildGroups(
            [Item("asset-1"), Item("asset-2", tradable: false), Item("asset-3")],
            [],
            purchases);

        Assert.DoesNotContain(groups, item => item.ActionDecision.Kind is "DeliveryPending" or "AwaitingBuyerAcceptance" or "Delivered");
        Assert.DoesNotContain(groups.SelectMany(item => item.StatusItems), item =>
            item.Status is "PendingDelivery" or "AwaitingBuyerAction" or "Delivered");
    }

    [Fact]
    public void TradeFlowPolicy_OnlyMarksActiveTradeFlows()
    {
        Assert.True(TradeFlowStatusPolicy.IsActiveIntakeStatus("Pending"));
        Assert.True(TradeFlowStatusPolicy.IsActiveIntakeStatus("AwaitingUserAction"));
        Assert.True(TradeFlowStatusPolicy.IsActiveIntakeStatus("ReceivedByBot"));
        Assert.False(TradeFlowStatusPolicy.IsActiveIntakeStatus("Credited"));
        Assert.False(TradeFlowStatusPolicy.IsActiveIntakeStatus("Failed"));
        Assert.False(TradeFlowStatusPolicy.IsActiveIntakeStatus("Canceled"));

        Assert.True(TradeFlowStatusPolicy.IsActiveDelivery(Purchase("asset-1", "PendingDelivery")));
        Assert.True(TradeFlowStatusPolicy.IsActiveDelivery(Purchase("asset-1", "AwaitingBuyerAction")));
        Assert.True(TradeFlowStatusPolicy.IsActiveDelivery(Purchase("asset-1", "DeliveryInEscrow")));
        Assert.False(TradeFlowStatusPolicy.IsActiveDelivery(Purchase("asset-1", "Delivered")));
        Assert.False(TradeFlowStatusPolicy.IsActiveDelivery(Purchase("asset-1", "DeliveryFailed")));

        var awaitingBotWithoutOffer = Purchase("asset-1", "AwaitingBotConfirmation");
        var awaitingBotWithOffer = Purchase("asset-1", "AwaitingBotConfirmation");
        awaitingBotWithOffer.DeliveryTradeOfferId = "123456";

        Assert.False(TradeFlowStatusPolicy.IsActiveDelivery(awaitingBotWithoutOffer));
        Assert.True(TradeFlowStatusPolicy.IsActiveDelivery(awaitingBotWithOffer));
    }

    [Fact]
    public void MarketPurchaseRecordModel_EnforcesActiveAssetReservationButAllowsSourceTradeReuse()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=skinmarket_model_test;Username=test;Password=test")
            .Options;
        using var dbContext = new AppDbContext(options);
        var entity = dbContext.Model.FindEntityType(typeof(MarketPurchaseRecord));
        Assert.NotNull(entity);

        var assetIndex = Assert.Single(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["AppId", "ContextId", "AssetId"]));
        Assert.True(assetIndex.IsUnique);
        Assert.Contains("\"Status\" = 'Sold'", assetIndex.GetFilter());
        Assert.Contains("'Delivered'", assetIndex.GetFilter());
        Assert.Contains("'PendingDelivery'", assetIndex.GetFilter());

        var sourceTradeIndex = Assert.Single(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["SourceTradeOperationId"]));
        Assert.False(sourceTradeIndex.IsUnique);
    }

    [Fact]
    public void UnknownInventoryState_IsDiagnosticNotPendingSale()
    {
        var group = Assert.Single(BuildGroups([Item("asset-1", tradable: null)], []));

        Assert.Equal(InventoryItemActionKinds.UnknownState, group.ActionDecision.Kind);
        Assert.True(group.ActionDecision.IsUnknown);
        Assert.DoesNotContain("Pending sale", group.ActionDecision.DiagnosticReason, StringComparison.OrdinalIgnoreCase);
    }

    private static List<GroupedInventoryItem> BuildGroups(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        IReadOnlyCollection<TradeOperation> operations,
        IReadOnlyCollection<MarketPurchaseRecord>? purchases = null)
    {
        return InventoryModel.BuildGroupedItems(
            items,
            operations.ToDictionary(item => item.AssetId, item => item, StringComparer.Ordinal),
            isTradeUrlConfigured: true);
    }

    private static SteamInventoryItemDto Item(string assetId, bool? tradable = true)
    {
        return new SteamInventoryItemDto
        {
            GameType = GameType.CS2,
            AssetId = assetId,
            ClassId = "class-1",
            InstanceId = "instance-1",
            Name = "AK-47 | Redline",
            MarketHashName = "AK-47 | Redline",
            MarketName = "AK-47 | Redline",
            Tradable = tradable,
            Marketable = true
        };
    }

    private static TradeOperation Operation(string assetId, string status, string? tradeOfferId = null)
    {
        return new TradeOperation
        {
            Id = Guid.NewGuid(),
            AppUserId = Guid.NewGuid(),
            SteamId = "76561198000000000",
            AssetId = assetId,
            ClassId = "class-1",
            InstanceId = "instance-1",
            AppId = 730,
            ContextId = "2",
            ItemName = "AK-47 | Redline",
            MarketHashName = "AK-47 | Redline",
            Status = status,
            TradeOfferId = tradeOfferId,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
    }

    private static MarketPurchaseRecord Purchase(string assetId, string deliveryStatus)
    {
        return new MarketPurchaseRecord
        {
            Id = Guid.NewGuid(),
            BuyerAppUserId = Guid.NewGuid(),
            GameType = GameType.CS2,
            AppId = 730,
            ContextId = "2",
            AssetId = assetId,
            ClassId = "class-1",
            InstanceId = "instance-1",
            ItemName = "AK-47 | Redline",
            MarketHashName = "AK-47 | Redline",
            Status = "Sold",
            DeliveryStatus = deliveryStatus,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
    }
}
