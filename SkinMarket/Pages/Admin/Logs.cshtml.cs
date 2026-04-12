using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
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

    private readonly AppDbContext _dbContext;

    public LogsModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public string? Level { get; set; }
    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 100;
    public List<AppLog> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var query = _dbContext.Logs
            .AsNoTracking()
            .Where(item => item.Source != null && InventorySources.Contains(item.Source))
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Level))
        {
            query = query.Where(item => item.Level == Level);
        }

        var take = Limit is 500 ? 500 : 100;
        Items = await query
            .OrderByDescending(item => item.TimestampUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
