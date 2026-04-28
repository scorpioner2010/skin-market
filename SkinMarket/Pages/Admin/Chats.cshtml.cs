using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class ChatsModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IItemChatService _itemChatService;
    private readonly IAppLogService _appLogService;

    public ChatsModel(AppDbContext dbContext, IItemChatService itemChatService, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _itemChatService = itemChatService;
        _appLogService = appLogService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? ThreadId { get; set; }
    [BindProperty]
    public AdminChatMessageInputModel MessageInput { get; set; } = new();
    [BindProperty]
    public Guid DeleteThreadId { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [TempData]
    public string? SuccessMessage { get; set; }

    public IReadOnlyList<ItemChatThreadSummary> Threads { get; private set; } = Array.Empty<ItemChatThreadSummary>();
    public ItemChatConversation? Conversation { get; private set; }
    public Guid? ActiveThreadId => Conversation?.Thread.Id ?? ThreadId;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPageAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        var adminUser = await LoadCurrentAdminAsync(cancellationToken);
        if (adminUser is null)
        {
            ErrorMessage = "Admin account was not found.";
            return RedirectToPage();
        }

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Message is invalid.";
            return RedirectToPage(new { threadId = MessageInput.ThreadId });
        }

        var result = await _itemChatService.SendAdminMessageAsync(
            adminUser.Id,
            MessageInput.ThreadId,
            MessageInput.Body,
            cancellationToken);

        if (result.Success)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { threadId = result.ThreadId ?? MessageInput.ThreadId });
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var adminUser = await LoadCurrentAdminAsync(cancellationToken);
        if (adminUser is null)
        {
            ErrorMessage = "Admin account was not found.";
            return RedirectToPage();
        }

        if (DeleteThreadId == Guid.Empty)
        {
            ErrorMessage = "Chat was not found.";
            return RedirectToPage();
        }

        var result = await _itemChatService.DeleteAdminThreadAsync(DeleteThreadId, cancellationToken);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
            return RedirectToPage(new { threadId = DeleteThreadId });
        }

        var thread = result.Thread;
        await _appLogService.WriteAsync(
            "Warning",
            $"Admin deleted item chat. ThreadId={result.ThreadId}; AppUserId={thread?.AppUserId}; UserSteamId={thread?.UserSteamId}; UserName={thread?.UserName}; Item={thread?.ItemName}; Messages={result.DeletedMessageCount}; DeletedByAppUserId={adminUser.Id}; DeletedBySteamId={adminUser.SteamId}",
            nameof(ChatsModel),
            cancellationToken: cancellationToken);

        SuccessMessage = result.Message;
        return RedirectToPage();
    }

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        Threads = await _itemChatService.GetAdminThreadsAsync(cancellationToken);
        Conversation = await _itemChatService.GetAdminConversationAsync(ThreadId, cancellationToken);
        if (ThreadId.HasValue && Conversation is null)
        {
            ErrorMessage = "Chat was not found.";
        }
    }

    private async Task<AppUser?> LoadCurrentAdminAsync(CancellationToken cancellationToken)
    {
        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        return await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SteamId == steamId && item.IsAdmin, cancellationToken);
    }

    public sealed class AdminChatMessageInputModel
    {
        [Required]
        public Guid ThreadId { get; set; }
        [Required]
        [StringLength(4000)]
        public string Body { get; set; } = string.Empty;
    }
}
