using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Services;

namespace SkinMarket.Pages.Admin;

public class BotHistoryModel : BotStatusModel
{
    public BotHistoryModel(
        AppDbContext dbContext,
        ISteamProfileService steamProfileService,
        ISteamBotInventoryClient steamBotInventoryClient,
        ISteamInventoryService steamInventoryService,
        ISteamTradeClient steamTradeClient,
        ISteamBotIntakeService steamBotIntakeService,
        ICreditService creditService,
        IMarketDeliveryService marketDeliveryService,
        IAppLogService appLogService,
        IBotServiceStatusClient botServiceStatusClient,
        IAppLogReader appLogReader,
        IGameCatalog gameCatalog,
        IOptions<SteamBotOptions> steamBotOptions)
        : base(
            dbContext,
            steamProfileService,
            steamBotInventoryClient,
            steamInventoryService,
            steamTradeClient,
            steamBotIntakeService,
            creditService,
            marketDeliveryService,
            appLogService,
            botServiceStatusClient,
            appLogReader,
            gameCatalog,
            steamBotOptions)
    {
    }
}
