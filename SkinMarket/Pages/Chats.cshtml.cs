using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Pages;

public class ChatsModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IItemChatService _itemChatService;
    private readonly AppRuntimeState _runtimeState;

    public ChatsModel(AppDbContext dbContext, IItemChatService itemChatService, AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _itemChatService = itemChatService;
        _runtimeState = runtimeState;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? ThreadId { get; set; }
    [BindProperty]
    public ChatMessageInputModel MessageInput { get; set; } = new();
    [TempData]
    public string? ErrorMessage { get; set; }
    [TempData]
    public string? SuccessMessage { get; set; }

    public IReadOnlyList<ItemChatThreadSummary> Threads { get; private set; } = Array.Empty<ItemChatThreadSummary>();
    public ItemChatConversation? Conversation { get; private set; }
    public Guid? ActiveThreadId => Conversation?.Thread.Id ?? ThreadId;
    public bool RequiresLogin { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPageAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return RedirectToPage();
        }

        var currentUser = await LoadCurrentUserAsync(cancellationToken);
        if (currentUser is null)
        {
            return RedirectToPage("/Auth/Login");
        }

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Message is invalid.";
            return RedirectToPage(new { threadId = MessageInput.ThreadId });
        }

        var result = await _itemChatService.SendUserMessageAsync(
            currentUser.Id,
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

    private async Task LoadPageAsync(CancellationToken cancellationToken)
    {
        if (_runtimeState.IsDegradedMode)
        {
            ErrorMessage = _runtimeState.ServiceUnavailableMessage;
            return;
        }

        var currentUser = await LoadCurrentUserAsync(cancellationToken);
        if (currentUser is null)
        {
            RequiresLogin = true;
            return;
        }

        Threads = await _itemChatService.GetUserThreadsAsync(currentUser.Id, cancellationToken);
        Conversation = await _itemChatService.GetUserConversationAsync(currentUser.Id, ThreadId, cancellationToken);
        if (ThreadId.HasValue && Conversation is null)
        {
            ErrorMessage = "Chat was not found.";
        }
    }

    private async Task<AppUser?> LoadCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return null;
        }

        var steamId = User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }

        return await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.SteamId == steamId, cancellationToken);
    }

    public sealed class ChatMessageInputModel
    {
        [Required]
        public Guid ThreadId { get; set; }
        [Required]
        [StringLength(4000)]
        public string Body { get; set; } = string.Empty;
    }
}
