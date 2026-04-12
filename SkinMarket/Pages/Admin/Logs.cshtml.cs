using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using SkinMarket.Contracts;
using SkinMarket.Models;
using SkinMarket.Services;
using SkinMarket.Pages;

namespace SkinMarket.Pages.Admin;

public class LogsModel : PageModel
{
    private static readonly string[] InventorySources =
    [
        nameof(SteamInventoryService),
        nameof(InventoryModel)
    ];

    private readonly IAppLogReader _appLogReader;

    public LogsModel(IAppLogReader appLogReader)
    {
        _appLogReader = appLogReader;
    }

    [BindProperty(SupportsGet = true)]
    public string? Level { get; set; }
    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 100;
    public List<AppLog> Items { get; private set; } = new();

    public void OnGet()
    {
        var take = Limit is 500 ? 500 : 100;
        Items = _appLogReader.GetRecent(take, Level, InventorySources).ToList();
    }
}
