namespace SkinMarket.Models;

public class ItemChatMessage
{
    public Guid Id { get; set; }
    public Guid ItemChatThreadId { get; set; }
    public Guid? AuthorAppUserId { get; set; }
    public string AuthorType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public ItemChatThread? ItemChatThread { get; set; }
    public AppUser? AuthorAppUser { get; set; }
}
