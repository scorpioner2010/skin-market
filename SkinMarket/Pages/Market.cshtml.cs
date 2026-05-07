using SkinMarket.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using SkinMarket.Models;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Localization;
using SkinMarket.Services;

namespace SkinMarket.Pages;

public class MarketModel : PageModel
{
    private readonly IMarketService _marketService;
    private readonly IMarketPurchaseService _marketPurchaseService;
    private readonly IMarketDeliveryService _marketDeliveryService;
    private readonly AppDbContext _dbContext;
    private readonly IBalanceService _balanceService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IGameCatalog _gameCatalog;
    private readonly AppRuntimeState _runtimeState;

    public MarketModel(
        IMarketService marketService,
        IMarketPurchaseService marketPurchaseService,
        IMarketDeliveryService marketDeliveryService,
        AppDbContext dbContext,
        IBalanceService balanceService,
        IStringLocalizer<SharedResource> localizer,
        IGameCatalog gameCatalog,
        AppRuntimeState runtimeState)
    {
        _marketService = marketService;
        _marketPurchaseService = marketPurchaseService;
        _marketDeliveryService = marketDeliveryService;
        _dbContext = dbContext;
        _balanceService = balanceService;
        _localizer = localizer;
        _gameCatalog = gameCatalog;
        _runtimeState = runtimeState;
    }

    public List<MarketListingItem> Items { get; private set; } = new();
    public List<GroupedMarketListingItem> GroupedItems { get; private set; } = new();
    public Dictionary<string, List<PriceSourceBreakdownItem>> PriceBreakdownsByMarketHashName { get; private set; } = new(StringComparer.Ordinal);
    public List<MarketPurchaseRecord> Purchases { get; private set; } = new();
    public Guid? CurrentUserId { get; private set; }
    public decimal CurrentBalance { get; private set; }
    public int TotalAvailableItemCount { get; private set; }
    public GameType CurrentGameType { get; private set; } = GameType.CS2;
    public string CurrentGameDisplayName { get; private set; } = string.Empty;
    public IReadOnlyList<GameDefinition> SupportedGames => _gameCatalog.SupportedGames;
    [BindProperty(SupportsGet = true)]
    public GameType Game { get; set; } = GameType.CS2;
    private static readonly string[] PriceBreakdownSources =
    [
        PriceSourceNames.Skinport,
        PriceSourceNames.DMarket,
        PriceSourceNames.Steam,
        PriceSourceNames.CSFloat
    ];
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public MarketPurchaseRequest PurchaseRequest { get; set; } = new();
    [BindProperty]
    public Guid MarketPurchaseId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        await LoadCurrentUserAsync(cancellationToken);
        var currentGame = _gameCatalog.Get(Game);
        Game = currentGame.Type;
        CurrentGameType = currentGame.Type;
        CurrentGameDisplayName = currentGame.DisplayName;
        Items = await _marketService.GetAvailableItemsAsync(CurrentGameType, cancellationToken);
        TotalAvailableItemCount = Items.Count;
        PriceBreakdownsByMarketHashName = await LoadPriceBreakdownsAsync(
            Items
                .Select(item => item.MarketHashName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            currentGame.SteamAppId,
            cancellationToken);
        GroupedItems = BuildGroupedItems(Items, CurrentUserId);
        if (CurrentUserId.HasValue)
        {
            Purchases = await _marketPurchaseService.GetRecentPurchasesAsync(CurrentUserId.Value, CurrentGameType, 10, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostBuyAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(PurchaseRequest.GameType);
        }

        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to buy market items.");
            return RedirectToCurrentGame(PurchaseRequest.GameType);
        }

        if (await HasActiveTradeFlowAsync(CurrentUserId.Value, cancellationToken))
        {
            ErrorMessage = "Finish or cancel the active trade offer before buying another item.";
            return RedirectToCurrentGame(PurchaseRequest.GameType);
        }

        var result = await _marketPurchaseService.PurchaseAsync(PurchaseRequest, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToCurrentGame(PurchaseRequest.GameType);
    }

    public async Task<IActionResult> OnPostCreateDeliveryTradeAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Game);
        }

        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create delivery trade.");
            return RedirectToCurrentGame(Game);
        }

        var result = await _marketDeliveryService.CreateDeliveryTradeAsync(MarketPurchaseId, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToCurrentGame(Game);
    }

    public async Task<IActionResult> OnPostConfirmDeliveredAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Game);
        }

        await LoadCurrentUserAsync(cancellationToken);
        if (!CurrentUserId.HasValue)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to confirm delivery.");
            return RedirectToCurrentGame(Game);
        }

        var result = await _marketDeliveryService.ConfirmDeliveredAsync(MarketPurchaseId, CurrentUserId.Value, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToCurrentGame(Game);
    }

    public string GetPriceNumberText(GroupedMarketListingItem item)
    {
        return item.HasReliablePrice && item.Price.HasValue
            ? $"${item.Price.Value:0.00}"
            : "-";
    }

    public string GetPriceStatusLabel(GroupedMarketListingItem item)
    {
        if (!item.HasReliablePrice)
        {
            return "No reliable price";
        }

        if (item.IsStale)
        {
            return "Stale";
        }

        if (item.IsCached)
        {
            return item.IsEstimated ? "Estimated cached" : "Cached";
        }

        return item.IsEstimated ? "Estimated" : "Live";
    }

    public string GetPriceSourceLabel(string? source)
    {
        return source switch
        {
            PriceSourceNames.CSFloat => "CSFloat",
            PriceSourceNames.Skinport => "Skinport",
            PriceSourceNames.Steam => "Steam",
            PriceSourceNames.DMarket => "DMarket",
            _ => "Unavailable"
        };
    }

    public IReadOnlyList<PriceSourceBreakdownItem> GetPriceBreakdown(string? marketHashName)
    {
        if (string.IsNullOrWhiteSpace(marketHashName))
        {
            return [];
        }

        var normalized = MarketHashNameUtility.Normalize(marketHashName) ?? marketHashName.Trim();
        return PriceBreakdownsByMarketHashName.GetValueOrDefault(normalized) ?? [];
    }

    public string GetBreakdownPriceText(PriceSourceBreakdownItem item)
    {
        return item.HasPrice && item.PriceUsd.HasValue
            ? $"${item.PriceUsd.Value:0.00}"
            : "-";
    }

    public string GetBreakdownMetaText(PriceSourceBreakdownItem item)
    {
        if (!item.HasPrice)
        {
            return string.IsNullOrWhiteSpace(item.FailureReason) ? item.Status : item.FailureReason;
        }

        var flags = new List<string> { item.PriceType };
        flags.Add(item.IsStale ? "stale" : item.IsEstimated ? "estimated" : item.Status);
        if (item.Quantity.HasValue)
        {
            flags.Add($"qty {item.Quantity.Value}");
        }
        else if (item.Volume.HasValue)
        {
            flags.Add($"vol {item.Volume.Value}");
        }

        return string.Join(" / ", flags.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private IActionResult RedirectToCurrentGame(GameType? gameType = null)
    {
        var game = _gameCatalog.Get(gameType ?? Game);
        return RedirectToPage(new { game = game.Type });
    }

    private async Task LoadCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return;
        }

        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SteamId == steamId, cancellationToken);

        if (user is null)
        {
            return;
        }

        CurrentUserId = user.Id;
        CurrentBalance = await _balanceService.GetBalanceAsync(user.Id, cancellationToken);
    }

    private async Task<bool> HasActiveTradeFlowAsync(Guid appUserId, CancellationToken cancellationToken)
    {
        var activeIntakeStatuses = new[]
        {
            "Pending",
            "BotPending",
            "AwaitingBotConfirmation",
            "TradeCreated",
            "AwaitingUserAction",
            "TradeAcceptedPendingReceipt",
            "ReceivedByBot",
            "InEscrow"
        };
        var activeDeliveryStatuses = new[]
        {
            "PendingDelivery",
            "DeliveryBotPending",
            "AwaitingBotConfirmation",
            "DeliveryTradeCreated",
            "AwaitingBuyerAction",
            "DeliveryInEscrow"
        };

        return await _dbContext.TradeOperations
                   .AsNoTracking()
                   .AnyAsync(operation => operation.AppUserId == appUserId && activeIntakeStatuses.Contains(operation.Status), cancellationToken) ||
               await _dbContext.MarketPurchaseRecords
                   .AsNoTracking()
                   .AnyAsync(item => item.BuyerAppUserId == appUserId &&
                                     item.DeliveryStatus != null &&
                                     activeDeliveryStatuses.Contains(item.DeliveryStatus), cancellationToken);
    }

    private async Task<Dictionary<string, List<PriceSourceBreakdownItem>>> LoadPriceBreakdownsAsync(
        IReadOnlyCollection<string> marketHashNames,
        int appId,
        CancellationToken cancellationToken)
    {
        if (marketHashNames.Count == 0)
        {
            return new Dictionary<string, List<PriceSourceBreakdownItem>>(StringComparer.Ordinal);
        }

        var normalizedMarketHashNames = marketHashNames
            .Select(MarketHashNameUtility.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedMarketHashNames.Count == 0)
        {
            return new Dictionary<string, List<PriceSourceBreakdownItem>>(StringComparer.Ordinal);
        }

        var snapshots = await _dbContext.PriceSnapshots
            .AsNoTracking()
            .Where(item => item.AppId == appId && normalizedMarketHashNames.Contains(item.MarketHashName))
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var snapshotsByMarketHashName = snapshots
            .GroupBy(item => MarketHashNameUtility.Normalize(item.MarketHashName) ?? item.MarketHashName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var breakdowns = new Dictionary<string, List<PriceSourceBreakdownItem>>(StringComparer.Ordinal);
        foreach (var normalizedName in normalizedMarketHashNames)
        {
            breakdowns[normalizedName] = BuildPriceBreakdownItems(
                snapshotsByMarketHashName.GetValueOrDefault(normalizedName) ?? [],
                now);
        }

        return breakdowns;
    }

    private static List<PriceSourceBreakdownItem> BuildPriceBreakdownItems(
        IReadOnlyCollection<PriceSnapshot> snapshots,
        DateTime now)
    {
        var rows = snapshots
            .Select(item => new PriceSourceBreakdownItem
            {
                Source = item.Source,
                PriceType = item.PriceType,
                Status = item.Status,
                HasPrice = item.HasPrice,
                PriceUsd = item.PriceUsd ?? item.Price,
                IsEstimated = item.IsEstimated,
                IsStale = item.ExpiresAtUtc <= now,
                ConfidenceScore = item.ConfidenceScore,
                Quantity = item.Quantity,
                Volume = item.Volume,
                UpdatedAtUtc = item.UpdatedAtUtc,
                ExpiresAtUtc = item.ExpiresAtUtc,
                FailureReason = item.FailureReason
            })
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(GetBreakdownSelectionRank)
                .ThenByDescending(item => item.ConfidenceScore)
                .ThenBy(item => item.PriceUsd)
                .First())
            .ToList();

        foreach (var source in PriceBreakdownSources)
        {
            if (rows.Any(item => string.Equals(item.Source, source, StringComparison.Ordinal)))
            {
                continue;
            }

            rows.Add(new PriceSourceBreakdownItem
            {
                Source = source,
                Status = "No snapshot",
                FailureReason = "No recent snapshot."
            });
        }

        return rows
            .OrderBy(item => GetPriceSourceOrder(item.Source))
            .ThenBy(item => item.PriceType, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetBreakdownSelectionRank(PriceSourceBreakdownItem item)
    {
        if (!item.IsEstimated && !item.IsStale && item.PriceType == PriceTypeNames.LowestListing)
        {
            return 10;
        }

        if (item.PriceType is PriceTypeNames.MedianSale or PriceTypeNames.AvgSale)
        {
            return item.IsStale ? 40 : 20;
        }

        if (item.PriceType is PriceTypeNames.Suggested or PriceTypeNames.BlendedEstimate)
        {
            return item.IsStale ? 50 : 30;
        }

        return item.IsStale ? 60 : 35;
    }

    private static int GetPriceSourceOrder(string? source)
    {
        return source switch
        {
            PriceSourceNames.Skinport => 10,
            PriceSourceNames.DMarket => 20,
            PriceSourceNames.Steam => 30,
            PriceSourceNames.CSFloat => 40,
            PriceSourceNames.Unavailable => 90,
            _ => 80
        };
    }

    private static List<GroupedMarketListingItem> BuildGroupedItems(
        IReadOnlyCollection<MarketListingItem> items,
        Guid? currentUserId)
    {
        return items
            .GroupBy(ItemGroupingKeyUtility.ForMarket, StringComparer.Ordinal)
            .Select(group =>
            {
                var entries = group.ToList();
                var ownedByCurrentUserCount = currentUserId.HasValue
                    ? entries.Count(item => item.SellerAppUserId == currentUserId.Value)
                    : 0;
                var buyableEntry = currentUserId.HasValue
                    ? entries.FirstOrDefault(item => item.SellerAppUserId != currentUserId.Value)
                    : entries.FirstOrDefault();
                var pricedBuyableEntry = currentUserId.HasValue
                    ? entries.FirstOrDefault(item => item.SellerAppUserId != currentUserId.Value && item.HasReliablePrice)
                    : entries.FirstOrDefault(item => item.HasReliablePrice);
                var representativeEntry = pricedBuyableEntry ?? buyableEntry ?? entries.First();

                return new GroupedMarketListingItem
                {
                    GameType = representativeEntry.GameType,
                    SourceTradeOperationId = representativeEntry.SourceTradeOperationId,
                    SellerAppUserId = representativeEntry.SellerAppUserId,
                    AppId = representativeEntry.AppId,
                    ContextId = representativeEntry.ContextId,
                    AssetId = representativeEntry.AssetId,
                    ClassId = representativeEntry.ClassId,
                    InstanceId = representativeEntry.InstanceId,
                    ItemName = representativeEntry.ItemName,
                    MarketHashName = representativeEntry.MarketHashName,
                    IconUrl = representativeEntry.IconUrl,
                    Price = representativeEntry.Price,
                    HasReliablePrice = representativeEntry.HasReliablePrice,
                    PriceDisplayText = representativeEntry.PriceDisplayText,
                    PriceSource = representativeEntry.PriceSource,
                    PriceType = representativeEntry.PriceType,
                    IsEstimated = representativeEntry.IsEstimated,
                    IsCached = representativeEntry.IsCached,
                    IsStale = representativeEntry.IsStale,
                    ConfidenceScore = representativeEntry.ConfidenceScore,
                    PriceFailureReason = representativeEntry.PriceFailureReason,
                    Tradable = representativeEntry.Tradable,
                    Marketable = representativeEntry.Marketable,
                    Quantity = entries.Count,
                    BuyableQuantity = currentUserId.HasValue
                        ? entries.Count(item => item.SellerAppUserId != currentUserId.Value && item.HasReliablePrice)
                        : entries.Count(item => item.HasReliablePrice),
                    CurrentUserOwnedQuantity = ownedByCurrentUserCount
                };
            })
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MarketHashName, StringComparer.Ordinal)
            .ToList();
    }
}
