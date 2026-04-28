using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public sealed class ItemChatService : IItemChatService
{
    private const int MaxMessageLength = 4000;
    private const int MaxPreviewLength = 300;
    private readonly AppDbContext _dbContext;

    public ItemChatService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ItemChatThread?> GetOrCreateThreadAsync(Guid appUserId, Guid serviceItemId, CancellationToken cancellationToken = default)
    {
        var existingThread = await _dbContext.ItemChatThreads
            .SingleOrDefaultAsync(
                thread => thread.AppUserId == appUserId && thread.ServiceItemId == serviceItemId,
                cancellationToken);
        if (existingThread is not null)
        {
            return existingThread;
        }

        var userExists = await _dbContext.AppUsers
            .AnyAsync(user => user.Id == appUserId, cancellationToken);
        if (!userExists)
        {
            return null;
        }

        var item = await _dbContext.ServiceItems
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == serviceItemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var thread = new ItemChatThread
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            ServiceItemId = item.Id,
            ItemNameSnapshot = item.Name,
            ItemImageUrlSnapshot = item.ImageUrl,
            ItemPriceSnapshot = item.Price,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _dbContext.ItemChatThreads.Add(thread);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return thread;
    }

    public async Task<IReadOnlyList<ItemChatThreadSummary>> GetUserThreadsAsync(Guid appUserId, CancellationToken cancellationToken = default)
    {
        var threads = await _dbContext.ItemChatThreads
            .AsNoTracking()
            .Include(thread => thread.AppUser)
            .Include(thread => thread.ServiceItem)
            .Where(thread => thread.AppUserId == appUserId)
            .OrderByDescending(thread => thread.LastMessageAtUtc ?? thread.UpdatedAtUtc)
            .ThenByDescending(thread => thread.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return threads.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<ItemChatThreadSummary>> GetAdminThreadsAsync(CancellationToken cancellationToken = default)
    {
        var threads = await _dbContext.ItemChatThreads
            .AsNoTracking()
            .Include(thread => thread.AppUser)
            .Include(thread => thread.ServiceItem)
            .OrderByDescending(thread => thread.LastMessageAtUtc ?? thread.UpdatedAtUtc)
            .ThenByDescending(thread => thread.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return threads.Select(ToSummary).ToList();
    }

    public async Task<ItemChatConversation?> GetUserConversationAsync(Guid appUserId, Guid? threadId, CancellationToken cancellationToken = default)
    {
        if (!threadId.HasValue)
        {
            var firstThread = await _dbContext.ItemChatThreads
                .AsNoTracking()
                .Where(thread => thread.AppUserId == appUserId)
                .OrderByDescending(thread => thread.LastMessageAtUtc ?? thread.UpdatedAtUtc)
                .ThenByDescending(thread => thread.CreatedAtUtc)
                .Select(thread => thread.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (firstThread == Guid.Empty)
            {
                return null;
            }

            threadId = firstThread;
        }

        var thread = await LoadConversationThreadAsync(threadId.Value, cancellationToken);
        if (thread is null || thread.AppUserId != appUserId)
        {
            return null;
        }

        return ToConversation(thread);
    }

    public async Task<ItemChatConversation?> GetAdminConversationAsync(Guid? threadId, CancellationToken cancellationToken = default)
    {
        if (!threadId.HasValue)
        {
            var firstThread = await _dbContext.ItemChatThreads
                .AsNoTracking()
                .OrderByDescending(thread => thread.LastMessageAtUtc ?? thread.UpdatedAtUtc)
                .ThenByDescending(thread => thread.CreatedAtUtc)
                .Select(thread => thread.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (firstThread == Guid.Empty)
            {
                return null;
            }

            threadId = firstThread;
        }

        var thread = await LoadConversationThreadAsync(threadId.Value, cancellationToken);
        return thread is null ? null : ToConversation(thread);
    }

    public async Task<ItemChatSendResult> SendUserMessageAsync(Guid appUserId, Guid threadId, string body, CancellationToken cancellationToken = default)
    {
        var normalizedBody = NormalizeMessage(body);
        if (string.IsNullOrWhiteSpace(normalizedBody))
        {
            return ItemChatSendResult.Fail("Message cannot be empty.", threadId);
        }

        var thread = await _dbContext.ItemChatThreads
            .SingleOrDefaultAsync(item => item.Id == threadId && item.AppUserId == appUserId, cancellationToken);
        if (thread is null)
        {
            return ItemChatSendResult.Fail("Chat was not found.", threadId);
        }

        await AddMessageAsync(thread, appUserId, ItemChatAuthorType.User, normalizedBody, cancellationToken);
        return ItemChatSendResult.Ok(thread.Id);
    }

    public async Task<ItemChatSendResult> SendAdminMessageAsync(Guid adminAppUserId, Guid threadId, string body, CancellationToken cancellationToken = default)
    {
        var normalizedBody = NormalizeMessage(body);
        if (string.IsNullOrWhiteSpace(normalizedBody))
        {
            return ItemChatSendResult.Fail("Message cannot be empty.", threadId);
        }

        var adminExists = await _dbContext.AppUsers
            .AnyAsync(user => user.Id == adminAppUserId && user.IsAdmin, cancellationToken);
        if (!adminExists)
        {
            return ItemChatSendResult.Fail("Admin account was not found.", threadId);
        }

        var thread = await _dbContext.ItemChatThreads
            .SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null)
        {
            return ItemChatSendResult.Fail("Chat was not found.", threadId);
        }

        await AddMessageAsync(thread, adminAppUserId, ItemChatAuthorType.Admin, normalizedBody, cancellationToken);
        return ItemChatSendResult.Ok(thread.Id);
    }

    public async Task<ItemChatDeleteResult> DeleteAdminThreadAsync(Guid threadId, CancellationToken cancellationToken = default)
    {
        var thread = await _dbContext.ItemChatThreads
            .Include(item => item.AppUser)
            .Include(item => item.ServiceItem)
            .SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);
        if (thread is null)
        {
            return ItemChatDeleteResult.Fail("Chat was not found.", threadId);
        }

        var deletedMessageCount = await _dbContext.ItemChatMessages
            .CountAsync(item => item.ItemChatThreadId == threadId, cancellationToken);
        var summary = ToSummary(thread);

        _dbContext.ItemChatThreads.Remove(thread);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ItemChatDeleteResult.Ok(summary, deletedMessageCount);
    }

    private async Task<ItemChatThread?> LoadConversationThreadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        return await _dbContext.ItemChatThreads
            .AsNoTracking()
            .Include(thread => thread.AppUser)
            .Include(thread => thread.ServiceItem)
            .Include(thread => thread.Messages)
            .ThenInclude(message => message.AuthorAppUser)
            .SingleOrDefaultAsync(thread => thread.Id == threadId, cancellationToken);
    }

    private async Task AddMessageAsync(
        ItemChatThread thread,
        Guid authorAppUserId,
        string authorType,
        string body,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _dbContext.ItemChatMessages.Add(new ItemChatMessage
        {
            Id = Guid.NewGuid(),
            ItemChatThreadId = thread.Id,
            AuthorAppUserId = authorAppUserId,
            AuthorType = authorType,
            Body = body,
            CreatedAtUtc = now
        });

        thread.LastMessageAtUtc = now;
        thread.LastMessagePreview = BuildPreview(body);
        thread.UpdatedAtUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length > MaxMessageLength
            ? normalized[..MaxMessageLength]
            : normalized;
    }

    private static string BuildPreview(string body)
    {
        var preview = body
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return preview.Length > MaxPreviewLength
            ? preview[..MaxPreviewLength]
            : preview;
    }

    private static ItemChatConversation ToConversation(ItemChatThread thread)
    {
        return new ItemChatConversation
        {
            Thread = ToSummary(thread),
            Messages = thread.Messages
                .OrderBy(message => message.CreatedAtUtc)
                .Select(ToMessageItem)
                .ToList()
        };
    }

    private static ItemChatThreadSummary ToSummary(ItemChatThread thread)
    {
        var userDisplayName = thread.AppUser is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(thread.AppUser.PersonaName)
                ? thread.AppUser.DisplayName
                : thread.AppUser.PersonaName;
        return new ItemChatThreadSummary
        {
            Id = thread.Id,
            AppUserId = thread.AppUserId,
            UserSteamId = thread.AppUser?.SteamId ?? string.Empty,
            UserDisplayName = userDisplayName,
            UserAvatarUrl = thread.AppUser?.AvatarUrl,
            ServiceItemId = thread.ServiceItemId,
            ItemName = thread.ServiceItem?.Name ?? thread.ItemNameSnapshot,
            ItemImageUrl = thread.ServiceItem?.ImageUrl ?? thread.ItemImageUrlSnapshot,
            ItemPrice = thread.ServiceItem?.Price ?? thread.ItemPriceSnapshot,
            LastMessagePreview = thread.LastMessagePreview,
            LastMessageAtUtc = thread.LastMessageAtUtc,
            CreatedAtUtc = thread.CreatedAtUtc,
            UpdatedAtUtc = thread.UpdatedAtUtc
        };
    }

    private static ItemChatMessageItem ToMessageItem(ItemChatMessage message)
    {
        var authorDisplayName = message.AuthorAppUser is null
            ? message.AuthorType
            : string.IsNullOrWhiteSpace(message.AuthorAppUser.PersonaName)
                ? message.AuthorAppUser.DisplayName
                : message.AuthorAppUser.PersonaName;
        return new ItemChatMessageItem
        {
            Id = message.Id,
            AuthorType = message.AuthorType,
            AuthorAppUserId = message.AuthorAppUserId,
            AuthorDisplayName = authorDisplayName,
            AuthorAvatarUrl = message.AuthorAppUser?.AvatarUrl,
            Body = message.Body,
            CreatedAtUtc = message.CreatedAtUtc
        };
    }
}
