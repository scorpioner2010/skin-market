using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
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
    private static readonly string[] ActiveDeliveryStatuses =
    [
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow"
    ];

    private readonly AppDbContext _dbContext;
    private readonly ISteamInventoryRefreshService _steamInventoryRefreshService;
    private readonly IInventoryPriceRefreshService _inventoryPriceRefreshService;
    private readonly ITradeOperationService _tradeOperationService;
    private readonly ISteamBotIntakeService _steamBotIntakeService;
    private readonly ISteamTradeClient _steamTradeClient;
    private readonly ICreditService _creditService;
    private readonly IAppLogService _appLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IGameCatalog _gameCatalog;
    private readonly AppRuntimeState _runtimeState;
    private readonly SteamInventoryRefreshOptions _inventoryRefreshOptions;

    public InventoryModel(
        AppDbContext dbContext,
        ISteamInventoryRefreshService steamInventoryRefreshService,
        IInventoryPriceRefreshService inventoryPriceRefreshService,
        ITradeOperationService tradeOperationService,
        ISteamBotIntakeService steamBotIntakeService,
        ISteamTradeClient steamTradeClient,
        ICreditService creditService,
        IAppLogService appLogService,
        IStringLocalizer<SharedResource> localizer,
        IGameCatalog gameCatalog,
        AppRuntimeState runtimeState,
        IOptions<SteamInventoryRefreshOptions> inventoryRefreshOptions)
    {
        _dbContext = dbContext;
        _steamInventoryRefreshService = steamInventoryRefreshService;
        _inventoryPriceRefreshService = inventoryPriceRefreshService;
        _tradeOperationService = tradeOperationService;
        _steamBotIntakeService = steamBotIntakeService;
        _steamTradeClient = steamTradeClient;
        _creditService = creditService;
        _appLogService = appLogService;
        _localizer = localizer;
        _gameCatalog = gameCatalog;
        _runtimeState = runtimeState;
        _inventoryRefreshOptions = inventoryRefreshOptions.Value;
    }

    public List<SteamInventoryItemDto> Items { get; private set; } = new();
    public List<GroupedInventoryItem> GroupedItems { get; private set; } = new();
    public Dictionary<string, ItemPriceResolutionResult> ResolvedPricesByAssetId { get; private set; } = new(StringComparer.Ordinal);
    public List<InventoryPriceItemRequest> PricePollingItems { get; private set; } = new();
    public List<TradeOperation> RecentOperations { get; private set; } = new();
    public Dictionary<string, TradeOperation> LatestOperationsByAssetId { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MarketPurchaseRecord> LatestPurchaseRecordsByAssetId { get; private set; } = new(StringComparer.Ordinal);
    public string? ErrorMessage { get; private set; }
    public string? WarningMessage { get; private set; }
    public string? InventorySnapshotWarningMessage { get; private set; }
    public string? InventoryRefreshLastErrorMessage { get; private set; }
    public DateTime? InventoryLastSuccessRefreshUtc { get; private set; }
    public DateTime? InventoryLastAttemptUtc { get; private set; }
    public DateTime? InventoryNextAllowedRefreshUtc { get; private set; }
    public bool InventoryRefreshInProgress { get; private set; }
    public bool InventoryRefreshRateLimited { get; private set; }
    public bool InventoryRefreshForced { get; private set; }
    public string? InventoryRefreshReason { get; private set; }
    public bool InventorySnapshotStale { get; private set; }
    public bool InventoryIsLoading { get; private set; }
    public bool InventoryRefreshTradeRelated => SteamInventoryRefreshReasons.IsTradeRelated(InventoryRefreshReason);
    public bool IsTradeUrlConfigured { get; private set; }
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? SellErrorMessage { get; set; }
    [TempData]
    public string? TradeStatusMessage { get; set; }
    public GameType CurrentGameType { get; private set; } = GameType.CS2;
    public string CurrentGameDisplayName { get; private set; } = string.Empty;
    public IReadOnlyList<GameDefinition> SupportedGames => _gameCatalog.SupportedGames;
    [BindProperty(SupportsGet = true)]
    public GameType Game { get; set; } = GameType.CS2;
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
            "DMarket" => "DMarket",
            _ => "Unavailable"
        };
    }

    public string GetPriceDisplayText(ItemPriceResolutionResult? price)
    {
        if (price?.Status == "Refreshing")
        {
            return "Refreshing...";
        }

        if (price?.HasPrice == true && price.DisplayPriceUsd is decimal amount)
        {
            var prefix = price.IsEstimated ? "~" : string.Empty;
            return $"{prefix}${amount:0.00}";
        }

        return "No reliable price";
    }

    public string GetPriceStatusLabel(ItemPriceResolutionResult? price)
    {
        if (price is null || !price.HasPrice)
        {
            return "Unavailable";
        }

        if (price.IsStale)
        {
            return "Stale";
        }

        if (price.IsEstimated)
        {
            return "Estimated";
        }

        if (price.IsCached)
        {
            return "Cached";
        }

        return GetPriceSourceLabel(price.Source);
    }

    public async Task<IActionResult> OnPostSellAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Input.GameType);
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create a sale request.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (string.IsNullOrWhiteSpace(appUser.TradeUrl))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL must be set before creating a sale request.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (await HasActiveTradeFlowAsync(appUser.Id, cancellationToken))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Finish or cancel the active trade offer before selling another item.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (string.IsNullOrWhiteSpace(Input.AssetId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Selected inventory item is invalid.");
            return RedirectToCurrentGame(Input.GameType);
        }

        var selectedGame = _gameCatalog.Get(Input.GameType);
        if (await _tradeOperationService.HasExistingSaleAsync(appUser.Id, selectedGame.Type, Input.AssetId, cancellationToken))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "This item already has a sale operation.");
            return RedirectToCurrentGame(selectedGame.Type);
        }

        var item = new SteamInventoryItemDto
        {
            GameType = selectedGame.Type,
            AssetId = Input.AssetId?.Trim() ?? string.Empty,
            ClassId = Input.ClassId?.Trim() ?? string.Empty,
            InstanceId = Input.InstanceId?.Trim() ?? string.Empty,
            Name = Input.ItemName?.Trim() ?? "Unknown Item",
            MarketHashName = MarketHashNameUtility.Normalize(Input.MarketHashName),
            IconUrl = string.IsNullOrWhiteSpace(Input.IconUrl) ? null : Input.IconUrl.Trim()
        };

        await _tradeOperationService.CreatePendingSaleAsync(appUser, item, cancellationToken);
        await TryEnqueueInventoryRefreshAsync(
            appUser.SteamId,
            selectedGame.Type,
            SteamInventoryRefreshReasons.UserSoldItem,
            cancellationToken,
            "sale request creation");
        SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request created. Intake trade will start automatically.");
        return RedirectToCurrentGame(selectedGame.Type);
    }

    public async Task<IActionResult> OnPostCreateTradeAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Input.GameType);
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create bot intake.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid.");
            return RedirectToCurrentGame(Input.GameType);
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

        return RedirectToCurrentGame(Input.GameType);
    }

    public async Task<IActionResult> OnPostCreditAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Input.GameType);
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to credit balance.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid.");
            return RedirectToCurrentGame(Input.GameType);
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

        return RedirectToCurrentGame(Input.GameType);
    }

    public async Task<IActionResult> OnPostRefreshTradeStatusAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            SellErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToCurrentGame(Input.GameType);
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create a sale request.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid.");
            return RedirectToCurrentGame(Input.GameType);
        }

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUser.Id, cancellationToken);
        if (operation is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Sale request was not found.");
            return RedirectToCurrentGame(Input.GameType);
        }

        if (string.IsNullOrWhiteSpace(operation.TradeOfferId))
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "This sale request does not have a Steam trade offer yet.");
            return RedirectToCurrentGame(Input.GameType);
        }

        var results = await _steamTradeClient.GetOfferStatusesAsync(
            new[]
            {
                new SteamTradeOfferStatusRequest
                {
                    OfferId = operation.TradeOfferId,
                    Flow = "intake"
                }
            },
            cancellationToken);
        var status = results.FirstOrDefault();
        if (status is null)
        {
            SellErrorMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Could not check Steam offer status. Bot service did not return a status.");
            return RedirectToCurrentGame(Input.GameType);
        }

        var previousOperationStatus = operation.Status;
        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var changed = SteamTradeSyncService.ApplyTradeOperationStatus(operation, status, transitionLogs);
        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (!string.Equals(previousOperationStatus, "ReceivedByBot", StringComparison.Ordinal) &&
                string.Equals(operation.Status, "ReceivedByBot", StringComparison.Ordinal))
            {
                var selectedGame = _gameCatalog.Get(Input.GameType);
                await TryEnqueueInventoryRefreshAsync(
                    appUser.SteamId,
                    selectedGame.Type,
                    SteamInventoryRefreshReasons.TradeAccepted,
                    cancellationToken,
                    "manual intake status refresh");
            }
        }

        foreach (var entry in transitionLogs)
        {
            await _appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        if (operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue)
        {
            var creditResult = await _creditService.ConfirmReceivedAndCreditAsync(operation.Id, appUser.Id, cancellationToken);
            await _appLogService.WriteAsync(
                creditResult.Success ? "Info" : "Warning",
                $"Manual status refresh credit result. TradeOperationId={operation.Id}; Success={creditResult.Success}; Status={creditResult.NewStatus}; OfferId={creditResult.TradeOfferId ?? "<null>"}; Message={creditResult.Message}",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);

            if (creditResult.Success)
            {
                SuccessMessage = UiTextLocalizer.LocalizeMessage(_localizer, creditResult.Message);
                return RedirectToCurrentGame(Input.GameType);
            }
        }

        TradeStatusMessage = BuildManualStatusMessage(operation, status);
        return RedirectToCurrentGame(Input.GameType);
    }

    public async Task<IActionResult> OnPostCancelIntakeAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            return BuildCancelIntakeResponse(false, _runtimeState.ServiceUnavailableMessage);
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return BuildCancelIntakeResponse(false, UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create a sale request."));
        }

        if (!Guid.TryParse(Input.TradeOperationId, out var tradeOperationId))
        {
            return BuildCancelIntakeResponse(false, UiTextLocalizer.LocalizeMessage(_localizer, "Sale request is invalid."));
        }

        var operation = await _dbContext.TradeOperations
            .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUser.Id, cancellationToken);
        if (operation is null)
        {
            return BuildCancelIntakeResponse(false, UiTextLocalizer.LocalizeMessage(_localizer, "Sale request was not found."));
        }

        if (string.IsNullOrWhiteSpace(operation.TradeOfferId) || !CanCancelIntakeStatus(operation.Status))
        {
            return BuildCancelIntakeResponse(false, UiTextLocalizer.LocalizeMessage(_localizer, $"Trade offer cannot be canceled from status {operation.Status}."));
        }

        var previousStatus = operation.Status;
        var result = await _steamTradeClient.CancelOfferAsync(
            operation.TradeOfferId,
            "intake",
            $"Seller canceled intake offer from inventory. TradeOperationId={operation.Id}",
            cancellationToken);

        if (!result.Success)
        {
            return BuildCancelIntakeResponse(false, result.Message);
        }

        operation.Status = "Failed";
        operation.ErrorMessage = $"Trade offer was canceled by seller. {result.Message}";
        operation.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Warning",
            $"Intake trade canceled by seller. TradeOperationId={operation.Id}; AppUserId={appUser.Id}; OfferId={operation.TradeOfferId}; PreviousStatus={previousStatus}; CancelState={result.State ?? "<null>"}; Message={result.Message}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        return BuildCancelIntakeResponse(true, UiTextLocalizer.LocalizeMessage(_localizer, "Trade offer was canceled."));
    }

    public async Task<IActionResult> OnGetSaleStatusAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            return new JsonResult(new
            {
                success = false,
                message = _runtimeState.ServiceUnavailableMessage
            });
        }

        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return new JsonResult(new
            {
                success = false,
                message = UiTextLocalizer.LocalizeMessage(_localizer, "Steam login is required to create a sale request.")
            });
        }

        var statusesToPoll = new[]
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

        var operations = await _dbContext.TradeOperations
            .AsNoTracking()
            .Where(operation =>
                operation.AppUserId == appUser.Id &&
                statusesToPoll.Contains(operation.Status))
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .Select(operation => new
            {
                id = operation.Id,
                assetId = operation.AssetId,
                itemName = operation.ItemName,
                status = operation.Status,
                statusText = UiTextLocalizer.LocalizeStatus(_localizer, operation.Status),
                detailText = BuildSaleStatusDetail(operation.Status, operation.TradeOfferId),
                tradeOfferId = operation.TradeOfferId,
                steamOfferUrl = BuildSteamOfferUrl(operation.TradeOfferId),
                accountTradeOffersUrl = BuildAccountTradeOffersUrl(),
                canCancel = CanCancelIntakeStatus(operation.Status) && operation.TradeOfferId != null,
                creditAmount = operation.CreditAmount,
                updatedAtUtc = operation.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return new JsonResult(new
        {
            success = true,
            operations
        });
    }

    private IActionResult BuildCancelIntakeResponse(bool success, string message)
    {
        if (string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.Ordinal))
        {
            Response.StatusCode = success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
            return new JsonResult(new
            {
                success,
                message
            });
        }

        if (success)
        {
            SuccessMessage = message;
        }
        else
        {
            SellErrorMessage = message;
        }

        return RedirectToCurrentGame(Input.GameType);
    }

    private IActionResult RedirectToCurrentGame(GameType? gameType = null)
    {
        var game = _gameCatalog.Get(gameType ?? Game);
        return RedirectToPage(new { game = game.Type });
    }

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        var appUser = await GetCurrentUserAsync(cancellationToken);
        if (appUser is null)
        {
            return;
        }

        var currentGame = _gameCatalog.Get(Game);
        Game = currentGame.Type;
        CurrentGameType = currentGame.Type;
        CurrentGameDisplayName = currentGame.DisplayName;
        await _appLogService.WriteAsync(
            "Info",
            $"Inventory page request started. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; GameKey={currentGame.Key}; TradeUrlConfigured={!string.IsNullOrWhiteSpace(appUser.TradeUrl)}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        IsTradeUrlConfigured = !string.IsNullOrWhiteSpace(appUser.TradeUrl);
        if (string.IsNullOrWhiteSpace(appUser.TradeUrl))
        {
            WarningMessage = UiTextLocalizer.LocalizeMessage(_localizer, "Trade URL is not set yet. Inventory still loads by SteamID.");
        }

        RecentOperations = await _tradeOperationService.GetRecentOperationsAsync(appUser.Id, CurrentGameType, 10, cancellationToken);
        LatestOperationsByAssetId = await _tradeOperationService.GetLatestOperationsByAssetIdAsync(appUser.Id, CurrentGameType, cancellationToken);
        await RefreshBuyerDeliveryStatusesAsync(appUser, currentGame, cancellationToken);
        await _appLogService.WriteAsync(
            "Info",
            $"Inventory prerequisites loaded. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; RecentOperations={RecentOperations.Count}; LatestOperationAssets={LatestOperationsByAssetId.Count}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        var snapshot = await _steamInventoryRefreshService.GetLatestSnapshotAsync(
            appUser.SteamId,
            CurrentGameType,
            cancellationToken);
        var refreshStatus = await _steamInventoryRefreshService.GetStatusAsync(
            appUser.SteamId,
            CurrentGameType,
            cancellationToken);
        ApplyInventoryRefreshStatus(refreshStatus);

        if (snapshot is null)
        {
            InventoryIsLoading = true;
            var enqueueStatus = await _steamInventoryRefreshService.EnqueueRefreshAsync(
                appUser.SteamId,
                CurrentGameType,
                SteamInventoryRefreshPriority.Normal,
                cancellationToken,
                reason: SteamInventoryRefreshReasons.InitialLoad);
            ApplyInventoryRefreshStatus(enqueueStatus);
            if (InventoryRefreshRateLimited)
            {
                InventorySnapshotWarningMessage = "Inventory is loading. Refresh skipped because cooldown is active.";
            }

            await _appLogService.WriteAsync(
                "Info",
                $"Inventory page has no snapshot yet. Refresh requested. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; IsRefreshing={InventoryRefreshInProgress}; RateLimited={InventoryRefreshRateLimited}; NextAllowed={InventoryNextAllowedRefreshUtc?.ToString("O") ?? "<null>"}",
                nameof(InventoryModel),
                cancellationToken: cancellationToken);
            return;
        }

        Items = snapshot.Items;
        InventoryLastSuccessRefreshUtc = snapshot.LastSuccessRefreshUtc;
        InventorySnapshotStale = snapshot.LastSuccessRefreshUtc <= DateTime.UtcNow.AddMinutes(-Math.Max(1, _inventoryRefreshOptions.AutoRefreshStaleMinutes));
        if (InventorySnapshotStale)
        {
            var failedAttemptCooldownMinutes = Math.Max(1, _inventoryRefreshOptions.FailedAutoRefreshAttemptCooldownMinutes);
            var recentFailedAttempt = InventoryLastAttemptUtc is not null &&
                                      InventoryLastAttemptUtc.Value >= DateTime.UtcNow.AddMinutes(-failedAttemptCooldownMinutes) &&
                                      !string.IsNullOrWhiteSpace(InventoryRefreshLastErrorMessage);
            if (InventoryRefreshRateLimited)
            {
                InventorySnapshotWarningMessage = "Inventory snapshot is stale. Refresh skipped because cooldown is active.";
            }
            else if (recentFailedAttempt)
            {
                InventorySnapshotWarningMessage = $"Inventory snapshot is stale. Refresh skipped because the last failed attempt was less than {failedAttemptCooldownMinutes} minutes ago.";
                await _appLogService.WriteAsync(
                    "Info",
                    $"Inventory stale auto-refresh skipped after recent failed attempt. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; LastAttempt={InventoryLastAttemptUtc:O}; LastError={InventoryRefreshLastErrorMessage ?? "<null>"}",
                    nameof(InventoryModel),
                    cancellationToken: cancellationToken);
            }
            else
            {
                var enqueueStatus = await _steamInventoryRefreshService.EnqueueRefreshAsync(
                    appUser.SteamId,
                    CurrentGameType,
                    SteamInventoryRefreshPriority.Normal,
                    cancellationToken,
                    reason: SteamInventoryRefreshReasons.AutoStale);
                ApplyInventoryRefreshStatus(enqueueStatus);
                InventorySnapshotWarningMessage = InventoryRefreshRateLimited
                    ? "Inventory snapshot is stale. Refresh skipped because cooldown is active."
                    : "Inventory snapshot is stale. A background refresh has been queued.";
            }
        }

        if (InventoryRefreshInProgress)
        {
            InventorySnapshotWarningMessage ??= InventoryRefreshTradeRelated
                ? "Syncing inventory after trade..."
                : "Updating inventory...";
        }

        if (!string.IsNullOrWhiteSpace(InventoryRefreshLastErrorMessage))
        {
            InventorySnapshotWarningMessage ??= InventoryRefreshLastErrorMessage;
        }

        await _appLogService.WriteAsync(
            "Info",
            $"Inventory snapshot accepted by page. AppUserId={appUser.Id}; SteamId={appUser.SteamId}; Game={(int)CurrentGameType}; LastSuccess={snapshot.LastSuccessRefreshUtc:O}; IsStale={InventorySnapshotStale}; ItemCount={Items.Count}; IsRefreshing={InventoryRefreshInProgress}; NextAllowed={InventoryNextAllowedRefreshUtc?.ToString("O") ?? "<null>"}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);

        await AppendPurchaseFallbackItemsAsync(appUser.Id, currentGame, Items, cancellationToken);
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

        GroupedItems = BuildGroupedItems(Items, LatestOperationsByAssetId, LatestPurchaseRecordsByAssetId, IsTradeUrlConfigured);
        await LogUnknownInventoryActionStatesAsync(appUser.Id, currentGame, GroupedItems, cancellationToken);
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

    private void ApplyInventoryRefreshStatus(SteamInventoryRefreshStatus status)
    {
        InventoryLastSuccessRefreshUtc = status.LastSuccessRefreshUtc ?? InventoryLastSuccessRefreshUtc;
        InventoryLastAttemptUtc = status.LastAttemptUtc;
        InventoryRefreshInProgress = status.IsRefreshing;
        InventoryRefreshRateLimited = status.IsRateLimited;
        InventoryRefreshForced = status.IsForced;
        InventoryRefreshReason = status.RefreshReason;
        InventoryNextAllowedRefreshUtc = status.NextAllowedRefreshUtc;
        InventoryRefreshLastErrorMessage = status.LastErrorMessage;
    }

    public string GetInventoryRefreshStatusText()
    {
        if (InventoryRefreshInProgress)
        {
            return InventoryRefreshTradeRelated
                ? "Syncing inventory after trade..."
                : "Updating inventory...";
        }

        if (InventoryLastSuccessRefreshUtc is null)
        {
            return "Loading inventory...";
        }

        return FormatInventoryLastUpdated();
    }

    public string FormatInventoryLastUpdated()
    {
        if (InventoryLastSuccessRefreshUtc is null)
        {
            return "Inventory has not been loaded yet.";
        }

        return $"Inventory last updated: {FormatAge(DateTime.UtcNow - InventoryLastSuccessRefreshUtc.Value)} ago.";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "less than 1 minute";
        }

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Floor(age.TotalMinutes));
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Floor(age.TotalHours));
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var days = Math.Max(1, (int)Math.Floor(age.TotalDays));
        return days == 1 ? "1 day" : $"{days} days";
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

    private async Task TryEnqueueInventoryRefreshAsync(
        string steamId,
        GameType gameType,
        string reason,
        CancellationToken cancellationToken,
        string source)
    {
        try
        {
            await _steamInventoryRefreshService.EnqueueRefreshAsync(
                steamId,
                gameType,
                SteamInventoryRefreshPriority.High,
                cancellationToken,
                forceFreshness: true,
                reason: reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _appLogService.WriteAsync(
                "Warning",
                $"Inventory refresh enqueue failed after {source}. SteamId={steamId}; GameType={(int)gameType}; Reason={reason}; Message={exception.Message}",
                nameof(InventoryModel),
                exception,
                CancellationToken.None);
        }
    }

    private async Task RefreshBuyerDeliveryStatusesAsync(
        AppUser appUser,
        GameDefinition game,
        CancellationToken cancellationToken)
    {
        var deliveryItems = await _dbContext.MarketPurchaseRecords
            .Where(item => item.BuyerAppUserId == appUser.Id &&
                           item.DeliveryTradeOfferId != null &&
                           (item.DeliveryStatus == "DeliveryBotPending" ||
                            item.DeliveryStatus == "AwaitingBotConfirmation" ||
                            item.DeliveryStatus == "DeliveryTradeCreated" ||
                            item.DeliveryStatus == "AwaitingBuyerAction" ||
                            item.DeliveryStatus == "DeliveryInEscrow"))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);
        if (deliveryItems.Count == 0)
        {
            return;
        }

        var requests = deliveryItems
            .Where(item => !string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId))
            .Select(item => new SteamTradeOfferStatusRequest
            {
                OfferId = item.DeliveryTradeOfferId!,
                Flow = "delivery"
            })
            .ToList();
        var statusResults = await _steamTradeClient.GetOfferStatusesAsync(requests, cancellationToken);
        var statusMap = statusResults.ToDictionary(
            item => item.OfferId,
            item => item,
            StringComparer.Ordinal);
        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var changed = false;
        var deliveredChanged = false;
        foreach (var item in deliveryItems)
        {
            if (string.IsNullOrWhiteSpace(item.DeliveryTradeOfferId) ||
                !statusMap.TryGetValue(item.DeliveryTradeOfferId, out var status))
            {
                continue;
            }

            var previousDeliveryStatus = item.DeliveryStatus;
            if (SteamTradeSyncService.ApplyDeliveryStatus(item, status, transitionLogs))
            {
                changed = true;
                deliveredChanged |= !string.Equals(previousDeliveryStatus, "Delivered", StringComparison.Ordinal) &&
                                    string.Equals(item.DeliveryStatus, "Delivered", StringComparison.Ordinal);
            }
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (deliveredChanged)
        {
            await TryEnqueueInventoryRefreshAsync(
                appUser.SteamId,
                game.Type,
                SteamInventoryRefreshReasons.ItemDelivered,
                cancellationToken,
                "buyer delivery status refresh");
        }

        foreach (var entry in transitionLogs)
        {
            await _appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }
    }

    private async Task AppendPurchaseFallbackItemsAsync(
        Guid appUserId,
        GameDefinition game,
        List<SteamInventoryItemDto> items,
        CancellationToken cancellationToken)
    {
        var visibleKeys = new HashSet<string>(
            items.Select(BuildInventoryFallbackKey),
            StringComparer.Ordinal);
        var purchases = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.BuyerAppUserId == appUserId &&
                           item.AppId == game.SteamAppId &&
                           item.ContextId == game.SteamContextId.ToString() &&
                           ((item.DeliveryStatus != null && ActiveDeliveryStatuses.Contains(item.DeliveryStatus)) ||
                            (item.DeliveryStatus == "Delivered" &&
                             item.DeliveredAtUtc != null &&
                             item.DeliveredAtUtc >= DateTime.UtcNow.AddDays(-14))))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(40)
            .ToListAsync(cancellationToken);

        foreach (var purchase in purchases)
        {
            var key = BuildInventoryFallbackKey(purchase.ClassId, purchase.InstanceId, purchase.MarketHashName, purchase.ItemName);
            var isActiveDelivery = IsActiveDeliveryStatus(purchase.DeliveryStatus);
            if (!isActiveDelivery && !visibleKeys.Add(key))
            {
                continue;
            }

            var syntheticAssetId = isActiveDelivery ? $"incoming:{purchase.Id}" : $"delivered:{purchase.Id}";
            items.Add(new SteamInventoryItemDto
            {
                GameType = purchase.GameType,
                AssetId = syntheticAssetId,
                ClassId = purchase.ClassId,
                InstanceId = purchase.InstanceId,
                Name = purchase.ItemName,
                MarketHashName = purchase.MarketHashName,
                MarketName = purchase.MarketHashName,
                IconUrl = purchase.IconUrl,
                Tradable = false,
                Marketable = false
            });
            LatestPurchaseRecordsByAssetId[syntheticAssetId] = purchase;
        }
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
        return await _dbContext.TradeOperations
                   .AsNoTracking()
                   .AnyAsync(operation => operation.AppUserId == appUserId && activeIntakeStatuses.Contains(operation.Status), cancellationToken) ||
               await _dbContext.MarketPurchaseRecords
                   .AsNoTracking()
                   .AnyAsync(item => item.BuyerAppUserId == appUserId &&
                                     item.DeliveryStatus != null &&
                                     ActiveDeliveryStatuses.Contains(item.DeliveryStatus), cancellationToken);
    }

    private static string BuildInventorySample(IEnumerable<SteamInventoryItemDto> items)
    {
        var sample = items
            .Take(5)
            .Select(item => $"Asset={item.AssetId}; Name={TruncateForLog(item.Name, 60)}; Hash={item.MarketHashName ?? item.MarketName ?? "<null>"}; Tradable={FormatNullableBool(item.Tradable)}; Marketable={FormatNullableBool(item.Marketable)}")
            .ToList();

        return sample.Count == 0 ? "<none>" : string.Join(" | ", sample);
    }

    private static string BuildInventoryFallbackKey(SteamInventoryItemDto item)
    {
        return BuildInventoryFallbackKey(item.ClassId, item.InstanceId, MarketHashNameUtility.ResolvePrimary(item), item.Name);
    }

    private static string BuildInventoryFallbackKey(string classId, string instanceId, string? marketHashName, string itemName)
    {
        return string.Join(
            "|",
            classId,
            instanceId,
            MarketHashNameUtility.Normalize(marketHashName) ?? string.Empty,
            itemName.Trim());
    }

    private static string BuildManualStatusMessage(TradeOperation operation, SteamTradeOfferStatusResult status)
    {
        if (status.State == "Active")
        {
            return "Steam still reports this offer as active. If you clicked Confirm, wait a few seconds and check again; otherwise complete the Steam confirmation popup or Steam mobile confirmation.";
        }

        return $"Steam status checked. SteamState={status.State}; AppStatus={operation.Status}; Message={status.Message ?? "<none>"}";
    }

    private static string BuildSteamOfferUrl(string? offerId)
    {
        return string.IsNullOrWhiteSpace(offerId)
            ? "https://steamcommunity.com/my/tradeoffers/"
            : $"https://steamcommunity.com/tradeoffer/{offerId}/";
    }

    private static string BuildAccountTradeOffersUrl()
    {
        return "https://steamcommunity.com/my/tradeoffers/";
    }

    private static string BuildSaleStatusDetail(string? status, string? offerId)
    {
        return status switch
        {
            "Pending" => "Waiting for bot to create Steam offer",
            "BotPending" => "Bot is creating Steam offer",
            "AwaitingBotConfirmation" => "Waiting for bot mobile confirmation",
            "TradeCreated" or "AwaitingUserAction" => "Open Steam and accept the trade offer",
            "TradeAcceptedPendingReceipt" or "ReceivedByBot" => "Waiting for bot receipt and credit",
            "InEscrow" => "Steam trade is in escrow",
            _ when string.IsNullOrWhiteSpace(offerId) => "Waiting for bot to create Steam offer",
            _ => "Waiting for next Steam trade step"
        };
    }

    private static bool CanCancelIntakeStatus(string? status)
    {
        return status is "AwaitingBotConfirmation" or "TradeCreated" or "AwaitingUserAction";
    }

    internal static List<GroupedInventoryItem> BuildGroupedItems(
        IReadOnlyCollection<SteamInventoryItemDto> items,
        IReadOnlyDictionary<string, TradeOperation> latestOperationsByAssetId,
        IReadOnlyDictionary<string, MarketPurchaseRecord> latestPurchasesByAssetId,
        bool isTradeUrlConfigured)
    {
        return items
            .Where(item =>
                !latestOperationsByAssetId.TryGetValue(item.AssetId, out var latestOperation) ||
                !RemovesInventoryItem(latestOperation.Status))
            .GroupBy(ItemGroupingKeyUtility.ForInventory, StringComparer.Ordinal)
            .Select(group =>
            {
                var entries = group.ToList();
                var representativeItem = entries.First();
                var availableItem = entries.FirstOrDefault(item =>
                    item.Tradable == true &&
                    (!latestOperationsByAssetId.TryGetValue(item.AssetId, out var latestOperation) ||
                     !BlocksInventoryItem(latestOperation.Status)));
                var createTradeOperation = entries
                    .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(operation => operation is not null && operation.Status == "Pending")
                    .OrderByDescending(operation => operation!.CreatedAtUtc)
                    .FirstOrDefault();
                var awaitingUserOperation = entries
                    .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(operation => operation is not null &&
                                        operation.Status == "AwaitingUserAction" &&
                                        !string.IsNullOrWhiteSpace(operation.TradeOfferId))
                    .OrderByDescending(operation => operation!.UpdatedAtUtc)
                    .FirstOrDefault();

                var blockedOperations = entries
                    .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(operation => operation is not null && BlocksInventoryItem(operation.Status))
                    .Cast<TradeOperation>()
                    .ToList();
                var activeTradeOperation = blockedOperations
                    .OrderByDescending(operation => operation.UpdatedAtUtc)
                    .FirstOrDefault();
                var blockedAssetIds = new HashSet<string>(
                    blockedOperations.Select(operation => operation.AssetId),
                    StringComparer.Ordinal);
                var purchaseStatuses = entries
                    .Select(item => latestPurchasesByAssetId.GetValueOrDefault(item.AssetId))
                    .Where(purchase => purchase is not null &&
                                       !string.IsNullOrWhiteSpace(purchase.DeliveryStatus))
                    .Cast<MarketPurchaseRecord>()
                    .ToList();
                var purchaseAssetIds = new HashSet<string>(
                    entries
                        .Where(item => latestPurchasesByAssetId.ContainsKey(item.AssetId))
                        .Select(item => item.AssetId),
                    StringComparer.Ordinal);

                var statusItems = blockedOperations
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

                statusItems.AddRange(purchaseStatuses
                    .GroupBy(purchase => purchase.DeliveryStatus!, StringComparer.Ordinal)
                    .Select(statusGroup => new GroupedInventoryStatusItem
                    {
                        Status = statusGroup.Key,
                        Quantity = statusGroup.Count()
                    }));

                var tradeProtectedCount = entries.Count(item =>
                    item.Tradable == false &&
                    !blockedAssetIds.Contains(item.AssetId) &&
                    !purchaseAssetIds.Contains(item.AssetId));
                if (tradeProtectedCount > 0)
                {
                    statusItems.Add(new GroupedInventoryStatusItem
                    {
                        Status = "TradeProtected",
                        Quantity = tradeProtectedCount
                    });
                }

                statusItems = statusItems
                    .OrderBy(item => GetInventoryStatusOrder(item.Status))
                    .ThenBy(item => item.Status, StringComparer.Ordinal)
                    .ToList();

                var readyCount = entries.Count(item =>
                    item.Tradable != false &&
                    !blockedAssetIds.Contains(item.AssetId) &&
                    !purchaseAssetIds.Contains(item.AssetId));
                if (readyCount > 0)
                {
                    statusItems.Insert(0, new GroupedInventoryStatusItem
                    {
                        IsReady = true,
                        Status = "Ready",
                        Quantity = readyCount
                    });
                }

                var item = new GroupedInventoryItem
                {
                    GroupKey = group.Key,
                    AssetIds = entries.Select(item => item.AssetId).ToList(),
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
                    AwaitingUserTradeOfferId = awaitingUserOperation?.TradeOfferId,
                    ActiveTradeOperationId = activeTradeOperation?.Id,
                    ActiveTradeStatus = activeTradeOperation?.Status,
                    ActiveTradeOfferId = activeTradeOperation?.TradeOfferId,
                    HasTradeProtected = tradeProtectedCount > 0,
                    HasIncomingDelivery = purchaseStatuses.Any(purchase => IsActiveDeliveryStatus(purchase.DeliveryStatus)),
                    HasDeliveredPurchase = purchaseStatuses.Any(purchase => string.Equals(purchase.DeliveryStatus, "Delivered", StringComparison.Ordinal)),
                    IncomingDeliveryStatus = purchaseStatuses
                        .Where(purchase => IsActiveDeliveryStatus(purchase.DeliveryStatus))
                        .OrderByDescending(purchase => purchase.UpdatedAtUtc)
                        .Select(purchase => purchase.DeliveryStatus)
                        .FirstOrDefault(),
                    HasWaitingForCredit = entries
                        .Select(item => latestOperationsByAssetId.GetValueOrDefault(item.AssetId))
                        .Any(operation => operation is not null &&
                                          BlocksInventoryItem(operation.Status) &&
                                          operation.Status == "ReceivedByBot" &&
                                          !operation.CreditedAtUtc.HasValue),
                    StatusItems = statusItems
                };

                item.ActionDecision = InventoryItemActionResolver.Resolve(item, isTradeUrlConfigured);
                return item;
            })
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MarketHashName, StringComparer.Ordinal)
            .ToList();
    }

    private async Task LogUnknownInventoryActionStatesAsync(
        Guid appUserId,
        GameDefinition game,
        IReadOnlyCollection<GroupedInventoryItem> groupedItems,
        CancellationToken cancellationToken)
    {
        var unknownItems = groupedItems
            .Where(item => item.ActionDecision.IsUnknown)
            .Take(20)
            .Select(item =>
                $"GroupKey={item.GroupKey}; RepresentativeAssetId={item.RepresentativeAssetId}; AssetIds={string.Join("|", item.AssetIds)}; ItemName={TruncateForLog(item.ItemName, 80)}; ActiveTradeStatus={item.ActiveTradeStatus ?? "<null>"}; IncomingDeliveryStatus={item.IncomingDeliveryStatus ?? "<null>"}; HasTradeProtected={item.HasTradeProtected}; HasDeliveredPurchase={item.HasDeliveredPurchase}; Reason={item.ActionDecision.DiagnosticReason}")
            .ToList();

        if (unknownItems.Count == 0)
        {
            return;
        }

        await _appLogService.WriteAsync(
            "Warning",
            $"Inventory UI reached unknown item action state. AppUserId={appUserId}; Game={game.Key}; Count={unknownItems.Count}; Items={string.Join(" || ", unknownItems)}",
            nameof(InventoryModel),
            cancellationToken: cancellationToken);
    }

    private static bool BlocksInventoryItem(string? status)
    {
        return status is not "Failed" and not "Credited";
    }

    private static bool RemovesInventoryItem(string? status)
    {
        return string.Equals(status, "Credited", StringComparison.Ordinal);
    }

    private static bool IsActiveDeliveryStatus(string? status)
    {
        return status is not null && ActiveDeliveryStatuses.Contains(status);
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
            "TradeProtected" => 6,
            "ReceivedByBot" => 7,
            "Credited" => 8,
            "PendingDelivery" => 9,
            "DeliveryBotPending" => 10,
            "DeliveryTradeCreated" => 11,
            "AwaitingBuyerAction" => 12,
            "DeliveryInEscrow" => 13,
            "Delivered" => 14,
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
        public GameType GameType { get; set; } = GameType.CS2;
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
