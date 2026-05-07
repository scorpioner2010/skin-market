using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class BotLogsModel : PageModel
{
    private readonly IBotServiceStatusClient _botServiceStatusClient;

    public BotLogsModel(IBotServiceStatusClient botServiceStatusClient)
    {
        _botServiceStatusClient = botServiceStatusClient;
    }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 100;

    [BindProperty(SupportsGet = true)]
    public string? Level { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? EventType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? TradeOperationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? OfferId { get; set; }

    public BotServiceStatusSnapshot BotStatus { get; private set; } = new();
    public BotServiceLogSnapshot BotLogs { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Limit = NormalizeLimit(Limit);
        Level = NormalizeFilter(Level);
        Source = NormalizeFilter(Source);
        EventType = NormalizeFilter(EventType);
        TradeOperationId = NormalizeFilter(TradeOperationId);
        OfferId = NormalizeFilter(OfferId);

        BotStatus = await _botServiceStatusClient.GetStatusAsync(cancellationToken);
        BotLogs = await _botServiceStatusClient.GetLogsAsync(new BotServiceLogQuery
        {
            Limit = Limit,
            Level = Level,
            Source = Source,
            EventType = EventType,
            TradeOperationId = TradeOperationId,
            OfferId = OfferId
        }, cancellationToken);
    }

    public string ResolveNotReadyReason()
    {
        if (!BotStatus.Reachable)
        {
            return "BotServiceHttpUnavailable";
        }

        if (BotStatus.Bot.Ready)
        {
            return "Ready";
        }

        return string.IsNullOrWhiteSpace(BotStatus.Bot.NotReadyReason)
            ? "BotServiceReturnedNotReady"
            : BotStatus.Bot.NotReadyReason;
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit != 500)
        {
            return 100;
        }

        return limit;
    }

    private static string? NormalizeFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
