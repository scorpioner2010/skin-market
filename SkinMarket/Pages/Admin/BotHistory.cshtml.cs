using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;

namespace SkinMarket.Pages.Admin;

public class BotHistoryModel : BotStatusModel
{
    public BotHistoryModel(
        AppDbContext dbContext,
        ISteamProfileService steamProfileService,
        IBotServiceStatusClient botServiceStatusClient,
        IAppLogReader appLogReader,
        IOptions<SteamBotOptions> steamBotOptions)
        : base(
            dbContext,
            steamProfileService,
            botServiceStatusClient,
            appLogReader,
            steamBotOptions)
    {
    }

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Limit = NormalizeLimit(Limit);
        SearchTerm = NormalizeSearchTerm(SearchTerm);
        HistoryMode = NormalizeHistoryMode(HistoryMode);
        await LoadHistoryAsync(Limit, cancellationToken);
    }
}
