using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Pages.Admin;

public class BotStatusModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly ISteamProfileService _steamProfileService;
    private readonly ISteamInventoryService _steamInventoryService;
    private readonly IBotServiceStatusClient _botServiceStatusClient;
    private readonly IAppLogReader _appLogReader;
    private readonly IGameCatalog _gameCatalog;
    private readonly SteamBotOptions _steamBotOptions;

    public BotStatusModel(
        AppDbContext dbContext,
        ISteamProfileService steamProfileService,
        ISteamInventoryService steamInventoryService,
        IBotServiceStatusClient botServiceStatusClient,
        IAppLogReader appLogReader,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions)
    {
        _dbContext = dbContext;
        _steamProfileService = steamProfileService;
        _steamInventoryService = steamInventoryService;
        _botServiceStatusClient = botServiceStatusClient;
        _appLogReader = appLogReader;
        _gameCatalog = gameCatalog;
        _steamBotOptions = steamBotOptions.Value;
    }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 50;

    public SteamProfileSummary? BotProfile { get; private set; }
    public List<BotPurchaseHistoryItem> PurchaseHistory { get; private set; } = new();
    public List<BotSaleHistoryItem> SaleHistory { get; private set; } = new();
    public BotServiceStatusSnapshot ServiceStatus { get; private set; } = new();
    public IReadOnlyList<AppLog> RecentAppIssues { get; private set; } = Array.Empty<AppLog>();
    public int InventoryItemCount { get; private set; }
    public int AvailableMarketItemCount { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? InventoryErrorMessage { get; private set; }
    public string BotSteamId => _steamBotOptions.BotSteamId;
    public string BotTradeUrl => _steamBotOptions.BotTradeUrl;
    public string ServiceUrl => _steamBotOptions.ServiceUrl;
    public bool BotEnabled => _steamBotOptions.Enabled;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var take = Limit <= 0 ? 50 : Math.Min(Limit, 200);
        ServiceStatus = await _botServiceStatusClient.GetStatusAsync(cancellationToken);
        RecentAppIssues = LoadRecentAppIssues();
        await LoadBotStatusAsync(cancellationToken);
        await LoadHistoryAsync(take, cancellationToken);
    }

    private async Task LoadBotStatusAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_steamBotOptions.BotSteamId))
        {
            ErrorMessage = "SteamBot:BotSteamId is not configured.";
            return;
        }

        BotProfile = await _steamProfileService.GetProfileAsync(_steamBotOptions.BotSteamId, cancellationToken);

        var game = _gameCatalog.Get(_gameCatalog.DefaultGameType);
        var inventory = await _steamInventoryService.GetInventoryAsync(_steamBotOptions.BotSteamId, game.Type, cancellationToken);
        if (!inventory.IsSuccess)
        {
            InventoryErrorMessage = inventory.ErrorMessage ?? "Bot inventory is unavailable.";
            return;
        }

        InventoryItemCount = inventory.Items.Count;
        var soldAssetIds = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .Where(item => item.AppId == game.SteamAppId && item.ContextId == game.SteamContextId.ToString())
            .Select(item => item.AssetId)
            .ToListAsync(cancellationToken);
        var soldAssetSet = new HashSet<string>(soldAssetIds, StringComparer.Ordinal);
        AvailableMarketItemCount = inventory.Items.Count(item => !soldAssetSet.Contains(item.AssetId));
    }

    private IReadOnlyList<AppLog> LoadRecentAppIssues()
    {
        var recent = _appLogReader.GetRecent(80, sources: BotDiagnosticsCatalog.AppLogSources);
        return BotDiagnosticsCatalog.FilterImportantAppEntries(recent, 12);
    }

    private async Task LoadHistoryAsync(int take, CancellationToken cancellationToken)
    {
        PurchaseHistory = await _dbContext.TradeOperations
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(take)
            .Select(item => new BotPurchaseHistoryItem
            {
                ItemName = item.ItemName,
                AssetId = item.AssetId,
                SellerDisplayName = item.AppUser != null
                    ? (item.AppUser.PersonaName ?? item.AppUser.DisplayName)
                    : item.SteamId,
                SellerSteamId = item.SteamId,
                SellerAvatarUrl = item.AppUser != null ? item.AppUser.AvatarUrl : null,
                Status = item.Status,
                CreditAmount = item.CreditAmount,
                CreatedAtUtc = item.CreatedAtUtc,
                CreditedAtUtc = item.CreditedAtUtc,
                TradeOfferId = item.TradeOfferId,
                ErrorMessage = item.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        SaleHistory = await _dbContext.MarketPurchaseRecords
            .AsNoTracking()
            .OrderByDescending(item => item.PurchasedAtUtc)
            .Take(take)
            .Select(item => new BotSaleHistoryItem
            {
                Id = item.Id,
                ItemName = item.ItemName,
                AssetId = item.AssetId,
                BuyerDisplayName = item.BuyerAppUser != null
                    ? (item.BuyerAppUser.PersonaName ?? item.BuyerAppUser.DisplayName)
                    : "Unknown buyer",
                BuyerSteamId = item.BuyerAppUser != null ? item.BuyerAppUser.SteamId : string.Empty,
                BuyerAvatarUrl = item.BuyerAppUser != null ? item.BuyerAppUser.AvatarUrl : null,
                SourceSellerDisplayName = item.SourceTradeOperation != null && item.SourceTradeOperation.AppUser != null
                    ? (item.SourceTradeOperation.AppUser.PersonaName ?? item.SourceTradeOperation.AppUser.DisplayName)
                    : null,
                Status = item.Status,
                DeliveryStatus = item.DeliveryStatus,
                Price = item.Price,
                PurchasedAtUtc = item.PurchasedAtUtc,
                DeliveryTradeOfferId = item.DeliveryTradeOfferId,
                DeliveredAtUtc = item.DeliveredAtUtc,
                DeliveryErrorMessage = item.DeliveryErrorMessage
            })
            .ToListAsync(cancellationToken);
    }
}
