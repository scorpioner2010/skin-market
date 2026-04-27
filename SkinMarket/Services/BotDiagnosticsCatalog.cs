using SkinMarket.Models;
using SkinMarket.Pages;
using SkinMarket.Pages.Admin;

namespace SkinMarket.Services;

public static class BotDiagnosticsCatalog
{
    public static readonly string[] AppLogSources =
    [
        nameof(BotServiceSteamTradeClient),
        nameof(BotServiceSteamInventoryClient),
        nameof(SteamTradeAutomationService),
        nameof(SteamTradeSyncService),
        nameof(LocalSteamBotHostService)
    ];
    public static readonly string[] WorkflowLogSources =
    [
        nameof(BotStatusModel),
        nameof(TradeOperationService),
        nameof(SteamBotIntakeService),
        nameof(BotServiceSteamTradeClient),
        nameof(SteamTradeAutomationService),
        nameof(SteamTradeSyncService),
        nameof(CreditService),
        nameof(MarketPurchaseService),
        nameof(MarketDeliveryService),
        nameof(BotServiceSteamInventoryClient),
        nameof(LocalSteamBotHostService),
        nameof(InventoryModel),
        nameof(MarketModel)
    ];

    public static bool IsImportantLevel(string? level)
    {
        return string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(level, "Critical", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<AppLog> FilterImportantAppEntries(IEnumerable<AppLog> entries, int take)
    {
        return entries
            .Where(entry => IsImportantLevel(entry.Level))
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(take)
            .ToList();
    }
}
