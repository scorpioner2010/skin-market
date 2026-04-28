namespace SkinMarket.Models;

public class ItemChatThread
{
    public Guid Id { get; set; }
    public Guid AppUserId { get; set; }
    public Guid? ServiceItemId { get; set; }
    public string ItemNameSnapshot { get; set; } = string.Empty;
    public string? ItemImageUrlSnapshot { get; set; }
    public decimal ItemPriceSnapshot { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime? LastMessageAtUtc { get; set; }
    public DateTime? LastUserMessageAtUtc { get; set; }
    public DateTime? LastAdminMessageAtUtc { get; set; }
    public DateTime? UserLastReadAtUtc { get; set; }
    public DateTime? AdminLastReadAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public AppUser? AppUser { get; set; }
    public ServiceItem? ServiceItem { get; set; }
    public ICollection<ItemChatMessage> Messages { get; set; } = new List<ItemChatMessage>();
}
