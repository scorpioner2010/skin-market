using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class ItemsModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly AppRuntimeState _runtimeState;

    public ItemsModel(AppDbContext dbContext, AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _runtimeState = runtimeState;
    }

    public List<ServiceItem> Items { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        Items = await _dbContext.ServiceItems
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ThenBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
