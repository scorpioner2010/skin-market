using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SkinMarket.Contracts;
using SkinMarket.Models;
using SkinMarket.Services;

namespace SkinMarket.Pages.Admin;

public class LogsModel : PageModel
{
    private readonly IAppLogReader _appLogReader;
    private readonly IBotServiceStatusClient _botServiceStatusClient;

    public LogsModel(
        IAppLogReader appLogReader,
        IBotServiceStatusClient botServiceStatusClient)
    {
        _appLogReader = appLogReader;
        _botServiceStatusClient = botServiceStatusClient;
    }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 50;

    public BotServiceStatusSnapshot BotStatus { get; private set; } = new();
    public IReadOnlyList<AppLog> AppEntries { get; private set; } = Array.Empty<AppLog>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var take = Limit <= 0 ? 50 : Math.Min(Limit, 200);
        BotStatus = await _botServiceStatusClient.GetStatusAsync(cancellationToken);
        var recent = _appLogReader.GetRecent(take * 4, sources: BotDiagnosticsCatalog.AppLogSources);
        AppEntries = BotDiagnosticsCatalog.FilterImportantAppEntries(recent, take);
    }
}
