using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Localization;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Pages;

public class InventoryModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly IInventoryPriceRefreshService _inventoryPriceRefreshService;
    private readonly ITradeOperationService _tradeOperationService;
    private readonly ISteamBotIntakeService _steamBotIntakeService;
    private readonly ICreditService _creditService;
    private readonly IAppLogService _appLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IGameCatalog _gameCatalog;
    private readonly AppRuntimeState _runtimeState;

    public InventoryModel(
        AppDbContext dbContext,
        ISteamInventoryService steamInventoryService,
        IInventoryPriceRefreshService inventoryPriceRefreshService,
        ITradeOperationService tradeOperationService,
        ISteamBotIntakeService steamBotIntakeService,
        ICreditService creditService,
        IAppLogService appLogService,
        IStringLocalizer<SharedResource> localizer,
        IGameCatalog gameCatalog,
        AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _steamInventoryService = steamInventoryService;
        _inventoryPriceRefreshService = inventoryPriceRefreshService;
        _tradeOperationService = tradeOperationService;
        _steamBotIntakeService = steamBotIntakeService;
        _creditService = creditService;
        _appLogService = appLogService;
        _localizer = localizer;
        _gameCatalog = gameCatalog;
        _runtimeState = runtimeState;
    }

    public List<SteamInventoryItemDto> Items { get; private set; } = new();
    public List<GroupedInventoryItem> GroupedItems { get; private set; } = new();
    public Dictionary<string, ItemPriceResolutionResult> ResolvedPricesByAssetId { get; private set; } = new(StringComparer.Ordinal);
    public List<InventoryPriceItemRequest> PricePollingItems { get; private set; } = new();
    public List<TradeOperation> RecentOperations { get; private set; } = new();
    public Dictionary<string, TradeOperation> LatestOperationsByAssetId { get; private set; } = new(StringComparer.Ordinal);
    public string? ErrorMessage { get; private set; }
    public string? WarningMessage { get; private set; }
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? SellErrorMessage { get; set; }
    public GameType CurrentGameType { get; private set; } = GameType.CS2;
    public string CurrentGameDisplayName { get; private set; } = string.Empty;
    [BindProperty]
    public SellInputModel Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            await _appLogService.WriteAsync(
                "Warning",
                "Inventory page request blocked because the application is running in degraded mode.",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);
            return;
        }

        await LoadPageAsync(cancellationToken);
    }

    public string GetPriceSourceLabel(string? source)
    {
        return source switch
        {
            "CSFloat" => "CSFloat",
            "Skinport" => "Skinport",
            "Steam" => "Steam",
            _ => "Unavailable"
        };
    }

    public async Task<IActionResult> OnPostSellAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToPage();
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create a sale request.");
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(appUser.TradeUrl))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL must be set before creating a sale request.");
            return RedirectToPage();
        }

        if (string.IsNullOrWhiteSpace(Input.AssetId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Selected inventory item is invalid.");
            return RedirectToPage();
        }

        if (await _tradeOperationService.HasExistingSaleAsync(appUser.Id, Input.AssetId, cancellationToken))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "This item already has a sale operation.");
            return RedirectToPage();
        }

        var item = new SteamInventoryItemDto
        {
            AssetId = Input.AssetId?.Trim() ?? string.Empty,
            ClassId = Input.ClassId?.Trim() ?? string.Empty,
            InstanceId = Input.InstanceId?.Trim() ?? string.Empty,
            Name = Input.ItemName?.Trim() ?? "Unknown Item",
            MarketHashName = MarketHashNameUtility.Normalize(Input.MarketHashName),
            IconUrl = string.IsNullOrWhiteSpace(Input.IconUrl) ? null : Input.IconUrl.Trim()
        };

        await _tradeOperationService.CreatePendingSaleAsync(appUser, item, cancellationToken);
        SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request created. Intake trade will start automatically.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateTradeAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToPage();
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create bot intake.");
            return RedirectToPage();
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid.");
            return RedirectToPage();
        }

        var result = await _steamBotIntakeService.CreateIntakeRequestAsync(tradeOperationId, appUser.Id, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreditAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToPage();
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to credit balance.");
            return RedirectToPage();
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid.");
            return RedirectToPage();
        }

        var result = await _creditService.ConfirmReceivedAndCreditAsync(tradeOperationId, appUser.Id, cancellationToken);
        if (result.Success)
        {
            SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }
        else
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.Message);
        }

        return RedirectToPage();
    }

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return;
        }

        var currentGame = _gameCatalog.Get(_gameCatalog.DefaultGameType);
        CurrentGameType = currentGame.Type;
        CurrentGameDisplayName = currentGame.DisplayName;
        await _appLogService.WriteAsync(
            "Info",
            $"Inventory page request started. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; GameKey={currentGame.Key}; TradeUrlConfigured={!string.IsNullOrWhiteSpace(appUser.TradeUrl)}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(appUser.TradeUrl))
        {
            WarningMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL is not set yet. Inventory still loads by SteamID.");
        }

        RecentOperations = await _tradeOperationService.GetRecentOperationsAsync(appUser.Id, 10, cancellationToken);
        LatestOperationsByAssetId = await _tradeOperationService.GetLatestOperationsByAssetIdAsync(appUser.Id, cancellationToken);
        await _appLogService.WriteAsync(
            "Info",
            $"Inventory prerequisites loaded. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; RecentOperations={RecentOperations.Count}; LatestOperationAssets={LatestOperationsByAssetId.Count}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        var result = await _steamInventoryService.GetInventoryAsync(appUser.SteamId, CurrentGameType, cancellationToken);
        if (!result.IsSuccess)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.ErrorMessage);
            await _appLogService.WriteAsync(
                "Warning",
                $"Inventory page load failed. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; Error={result.ErrorMessage}",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);
            return;
        }

        Items = result.Items;
        var marketHashNames = Items
            .Select(MarketHashNameUtility.ResolvePrimary)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await _appLogService.WriteAsync(
            Items.Count == 0 ? "Warning" : "Info",
            $"Inventory data accepted by page. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; ItemCount={Items.Count}; DistinctMarketHashes={marketHashNames.Count}; Sample={BuildInventorySample(Items)}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        var pricesByMarketHashName = await _inventoryPriceRefreshService.GetCurrentPricesAsync(marketHashNames, CurrentGameType, cancellationToken);
        var refreshTargets = new List<string>();
        foreach (var item in Items)
        {
            var marketHashName = MarketHashNameUtility.ResolvePrimary(item);
            if (!string.IsNullOrWhiteSpace(marketHashName) &&
                pricesByMarketHashName.TryGetValue(marketHashName, out var resolvedPrice))
            {
                if (resolvedPrice.NeedsRefresh && !resolvedPrice.HasPrice)
                {
                    resolvedPrice.Status = "Refreshing";
                }

                ResolvedPricesByAssetId[item.AssetId] = resolvedPrice;
                if (resolvedPrice.NeedsRefresh || resolvedPrice.Status == "Refreshing")
                {
                    refreshTargets.Add(marketHashName);
                    PricePollingItems.Add(new InventoryPriceItemRequest
                    {
                        AssetId = item.AssetId,
                        MarketHashName = marketHashName
                    });
                }
                continue;
            }

            ResolvedPricesByAssetId[item.AssetId] = new ItemPriceResolutionResult
            {
                HasPrice = false,
                Currency = "USD",
                Source = "Unavailable",
                Status = "Refreshing",
                FailureReason = "Missing cached price.",
                NeedsRefresh = true
            };

            if (!string.IsNullOrWhiteSpace(marketHashName))
            {
                refreshTargets.Add(marketHashName);
                PricePollingItems.Add(new InventoryPriceItemRequest
                {
                    AssetId = item.AssetId,
                    MarketHashName = marketHashName
                });
            }
        }

        if (refreshTargets.Count > 0)
        {
            await _inventoryPriceRefreshService.QueueRefreshAsync(refreshTargets.Distinct(StringComparer.Ordinal).ToList(), CurrentGameType, cancellationToken);
        }

        GroupedItems = BuildGroupedItems(Items, LatestOperationsByAssetId);
        PricePollingItems = GroupedItems
            .Select(group =>
            {
                ResolvedPricesByAssetId.TryGetValue(group.RepresentativeAssetId, out var resolvedPrice);
                return new
                {
                    Group = group,
                    Price = resolvedPrice
                };
            })
            .Where(item =>
                item.Price is not null &&
                !string.IsNullOrWhiteSpace(item.Group.MarketHashName) &&
                (item.Price.NeedsRefresh || item.Price.Status == "Refreshing"))
            .Select(item => new InventoryPriceItemRequest
            {
                AssetId = item.Group.RepresentativeAssetId,
                MarketHashName = item.Group.MarketHashName!
            })
            .ToList();

        var distinctRefreshTargets = refreshTargets
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await _appLogService.WriteAsync(
            "Info",
            $"Inventory price state prepared. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; ResolvedItems={ResolvedPricesByAssetId.Count}; PricePollingCount={PricePollingItems.Count}; RefreshTargets={distinctRefreshTargets.Count}; StatusSummary={BuildPriceStatusSummary(ResolvedPricesByAssetId.Values)}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        await _appLogService.WriteAsync(
            Items.Count == 0 ? "Warning" : "Info",
            $"Inventory page loaded. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; ItemCount={Items.Count}; RecentOperations={RecentOperations.Count}; PricePollingCount={PricePollingItems.Count}; RefreshTargets={distinctRefreshTargets.Count}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);
    }

    private async Task<AppUser?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return null;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "SteamID is not available for the current session.");
            await _appLogService.WriteAsync(
                "Warning",
                "Inventory page could not resolve SteamId from the authenticated session.",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);
            return null;
        }

        var appUser = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);

        if (appUser is null)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Local user profile was not found.");
            await _appLogService.WriteAsync(
                "Warning",
                $"Inventory page could not find a local user profile for SteamId={steamId}.",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);
        }

        return appUser;
    }

    private static string BuildInventorySample(IEnumerable<SteamInventoryItemDto> items)
    {
        var sample = items
            .Take(5)
            .Select(item => $"Asset={item.AssetId}; Name={TruncateForLog(item.Name, 60)}; Hash={item.MarketHashName ?? item.MarketName ?? "<null>"}; Tradable={FormatNullableBool(item.Tradable)}; Marketable={FormatNullableBool(item.Marketable)}")
            .ToList();

        return sample.Count == 0 ? "<none>" : string.Join(" | ", sample);
    }

    private static List<GroupedInventoryItem> BuildGroupedItems(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        IReadOnlyDictionary<string, TradeOperation> latestOperationsByAssetId)
    {
        return items
            .GroupBy(ItemGroupingKeyUtility.ForInventory, StringComparer.Ordinal)
            .Select(group =>
            {
                var entries = group.ToList();
                var representativeItem = entries.First();
                var availableItem = entries.FirstOrDefault(item => !latestOperationsByAssetId.ContainsKey(item.AssetId));
                var createTradeOperation = entries
                    .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(operation => operation is not null && (operation.Status == "Pending" || operation.Status == "Failed"))
                    .OrderByDescending(operation => operation!.CreatedAtUtc)
                    .FirstOrDefault();

                var statusItems = entries
                    .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(operation => operation is not null)
                    .GroupBy(operation => operation!.Status, StringComparer.Ordinal)
                    .Select(statusGroup => new GroupedInventoryStatusItem
                    {
                        Status = statusGroup.Key,
                        Quantity = statusGroup.Count(),
                        CreditAmountTotal = statusGroup.Sum(operation => operation!.CreditAmount)
                    })
                    .OrderBy(item => GetInventoryStatusOrder(item.Status))
                    .ThenBy(item => item.Status, StringComparer.Ordinal)
                    .ToList();

                var readyCount = entries.Count - statusItems.Sum(item => item.Quantity);
                if (readyCount > 0)
                {
                    statusItems.Insert(0, new GroupedInventoryStatusItem
                    {
                        IsReady = true,
                        Status = "Ready",
                        Quantity = readyCount
                    });
                }

                return new GroupedInventoryItem
                {
                    GroupKey = group.Key,
                    RepresentativeAssetId = representativeItem.AssetId,
                    ItemName = representativeItem.Name,
                    MarketHashName = MarketHashNameUtility.ResolvePrimary(representativeItem),
                    IconUrl = representativeItem.IconUrl,
                    ClassId = representativeItem.ClassId,
                    InstanceId = representativeItem.InstanceId,
                    Tradable = representativeItem.Tradable,
                    Marketable = representativeItem.Marketable,
                    Quantity = entries.Count,
                    SellAssetId = availableItem?.AssetId,
                    SellClassId = availableItem?.ClassId,
                    SellInstanceId = availableItem?.InstanceId,
                    SellItemName = availableItem?.Name,
                    SellMarketHashName = availableItem is null ? null : MarketHashNameUtility.ResolvePrimary(availableItem),
                    SellIconUrl = availableItem?.IconUrl,
                    CreateTradeOperationId = createTradeOperation?.Id,
                    CreateTradeStatus = createTradeOperation?.Status,
                    HasWaitingForCredit = entries
                        .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                        .Any(operation => operation is not null && operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue),
                    StatusItems = statusItems
                };
            })
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MarketHashName, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetInventoryStatusOrder(string status)
    {
        return status switch
        {
            "Pending" => 1,
            "Failed" => 2,
            "BotPending" => 3,
            "AwaitingBotConfirmation" => 4,
            "TradeCreated" => 5,
            "AwaitingUserAction" => 6,
            "ReceivedByBot" => 7,
            "Credited" => 8,
            _ => 20
        };
    }

    private static string BuildPriceStatusSummary(IEnumerable<ItemPriceResolutionResult> results)
    {
        var summary = results
            .GroupBy(result => $"{result.Status}/{result.Source}")
            .OrderByDescending(group => group.Count())
            .Take(8)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToList();

        return summary.Count == 0 ? "<none>" : string.Join(" | ", summary);
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "<null>";
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    public class SellInputModel
    {
        [Required]
        public string? AssetId { get; set; }
        public string? ClassId { get; set; }
        public string? InstanceId { get; set; }
        public string? ItemName { get; set; }
        public string? MarketHashName { get; set; }
        public string? IconUrl { get; set; }
        public string? TradeOperationId { get; set; }
    }
}
