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
    public int Limit { get; set; } = 100;

    public BotServiceStatusSnapshot BotStatus { get; private set; } = new();
    public IReadOnlyList<AppLog> AppEntries { get; private set; } = Array.Empty<AppLog>();
    public IReadOnlyList<AppLog> InventoryEntries { get; private set; } = Array.Empty<AppLog>();
    public IReadOnlyList<AppLog> WorkflowEntries { get; private set; } = Array.Empty<AppLog>();
    public IReadOnlyDictionary<string, string> HostingDetails { get; private set; } = new Dictionary<string, string>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var take = Limit <= 0 ? 100 : Math.Min(Limit, 500);
        BotStatus = await _botServiceStatusClient.GetStatusAsync(cancellationToken);
        var recent = _appLogReader.GetRecent(take * 4, sources: BotDiagnosticsCatalog.AppLogSources);
        AppEntries = BotDiagnosticsCatalog.FilterImportantAppEntries(recent, take);
        InventoryEntries = _appLogReader.GetRecent(take, sources: BotDiagnosticsCatalog.InventoryLogSources);
        WorkflowEntries = _appLogReader.GetRecent(Math.Max(take, 40), sources: BotDiagnosticsCatalog.WorkflowLogSources);
        HostingDetails = BuildHostingDetails();
    }

    private static IReadOnlyDictionary<string, string> BuildHostingDetails()
    {
        var renderService = FirstConfiguredEnvironmentValue("RENDER_SERVICE_NAME", "RENDER_SERVICE_ID");
        var renderRegion = FirstConfiguredEnvironmentValue("RENDER_REGION");
        var renderCommit = FirstConfiguredEnvironmentValue("RENDER_GIT_COMMIT");
        var isRender = HasEnvironmentValue("RENDER") ||
                       !string.IsNullOrWhiteSpace(renderService) ||
                       !string.IsNullOrWhiteSpace(renderRegion);

        return new Dictionary<string, string>
        {
            ["Server Time UTC"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ["Environment"] = FirstConfiguredEnvironmentValue("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT") ?? "Unknown",
            ["Render"] = isRender ? "Yes" : "No",
            ["Render Service"] = renderService ?? "Not set",
            ["Render Region"] = renderRegion ?? "Not set",
            ["Render Commit"] = renderCommit ?? "Not set",
            ["Machine"] = Environment.MachineName
        };
    }

    private static bool HasEnvironmentValue(string key)
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key));
    }

    private static string? FirstConfiguredEnvironmentValue(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
