namespace SkinMarket.Models;

public sealed class ItemChatThreadSummary
{
    public Guid Id { get; init; }
    public Guid AppUserId { get; init; }
    public string UserSteamId { get; init; } = string.Empty;
    public string UserDisplayName { get; init; } = string.Empty;
    public string? UserAvatarUrl { get; init; }
    public Guid? ServiceItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemImageUrl { get; init; }
    public decimal ItemPrice { get; init; }
    public string? LastMessagePreview { get; init; }
    public DateTime? LastMessageAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public string UserName => string.IsNullOrWhiteSpace(UserDisplayName) ? UserSteamId : UserDisplayName;
}

public sealed class ItemChatMessageItem
{
    public Guid Id { get; init; }
    public string AuthorType { get; init; } = string.Empty;
    public Guid? AuthorAppUserId { get; init; }
    public string AuthorDisplayName { get; init; } = string.Empty;
    public string? AuthorAvatarUrl { get; init; }
    public string Body { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class ItemChatConversation
{
    public ItemChatThreadSummary Thread { get; init; } = new();
    public IReadOnlyList<ItemChatMessageItem> Messages { get; init; } = Array.Empty<ItemChatMessageItem>();
}

public sealed class ItemChatSendResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? ThreadId { get; init; }

    public static ItemChatSendResult Ok(Guid threadId, string message = "Message sent.")
    {
        return new ItemChatSendResult
        {
            Success = true,
            Message = message,
            ThreadId = threadId
        };
    }

    public static ItemChatSendResult Fail(string message, Guid? threadId = null)
    {
        return new ItemChatSendResult
        {
            Success = false,
            Message = message,
            ThreadId = threadId
        };
    }
}

public sealed class ItemChatDeleteResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? ThreadId { get; init; }
    public ItemChatThreadSummary? Thread { get; init; }
    public int DeletedMessageCount { get; init; }

    public static ItemChatDeleteResult Ok(ItemChatThreadSummary thread, int deletedMessageCount, string message = "Chat deleted.")
    {
        return new ItemChatDeleteResult
        {
            Success = true,
            Message = message,
            ThreadId = thread.Id,
            Thread = thread,
            DeletedMessageCount = deletedMessageCount
        };
    }

    public static ItemChatDeleteResult Fail(string message, Guid? threadId = null)
    {
        return new ItemChatDeleteResult
        {
            Success = false,
            Message = message,
            ThreadId = threadId
        };
    }
}
