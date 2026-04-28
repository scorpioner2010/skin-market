using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class ItemsModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly AppRuntimeState _runtimeState;
    private readonly IItemChatService _itemChatService;

    public ItemsModel(AppDbContext dbContext, AppRuntimeState runtimeState, IItemChatService itemChatService)
    {
        _dbContext = dbContext;
        _runtimeState = runtimeState;
        _itemChatService = itemChatService;
    }

    public List<ServiceItem> Items { get; private set; } = new();
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public Guid ChatItemId { get; set; }

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

    public async Task<IActionResult> OnPostOpenChatAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToPage();
        }

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return RedirectToPage("/Auth/Login");
        }

        var currentUser = await LoadCurrentUserAsync(cancellationToken);
        if (currentUser is null)
        {
            ErrorMessage = "Local user profile was not found.";
            return RedirectToPage();
        }

        var thread = await _itemChatService.GetOrCreateThreadAsync(currentUser.Id, ChatItemId, cancellationToken);
        if (thread is null)
        {
            ErrorMessage = "Item was not found.";
            return RedirectToPage();
        }

        return RedirectToPage("/Chats", new { threadId = thread.Id });
    }

    private async Task<AppUser?> LoadCurrentUserAsync(CancellationToken cancellationToken)
    {
        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        return await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SteamId == steamId, cancellationToken);
    }
}
