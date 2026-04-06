using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class LogsModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public LogsModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [BindProperty(SupportsGet = true)]
    public string? Level { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }
    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = 100;
    public List<AppLog> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var query = _dbContext.Logs
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Level))
        {
            query = query.Where(item => item.Level == Level);
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            var sourceFilter = Source.Trim();
            query = query.Where(item => item.Source != null && EF.Functions.ILike(item.Source, $"%{sourceFilter}%"));
        }

        var take = Limit is 500 ? 500 : 100;
        Items = await query
            .OrderByDescending(item => item.TimestampUtc)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
