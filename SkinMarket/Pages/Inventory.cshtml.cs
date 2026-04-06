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
        _localizer = localizer;
        _gameCatalog = gameCatalog;
        _runtimeState = runtimeState;
    }

    public List<SteamInventoryItemDto> Items { get; private set; } = new();
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
        SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request created.");
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

        if (string.IsNullOrWhiteSpace(appUser.TradeUrl))
        {
            WarningMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL is not set yet. Inventory still loads by SteamID.");
        }

        RecentOperations = await _tradeOperationService.GetRecentOperationsAsync(appUser.Id, 10, cancellationToken);
        LatestOperationsByAssetId = await _tradeOperationService.GetLatestOperationsByAssetIdAsync(appUser.Id, cancellationToken);

        var result = await _steamInventoryService.GetInventoryAsync(appUser.SteamId, CurrentGameType, cancellationToken);
        if (!result.IsSuccess)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, result.ErrorMessage);
            return;
        }

        Items = result.Items;
        var marketHashNames = Items
            .Select(MarketHashNameUtility.ResolvePrimary)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

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
            return null;
        }

        var appUser = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);

        if (appUser is null)
        {
            ErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Local user profile was not found.");
        }

        return appUser;
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
