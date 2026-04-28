namespace SkinMarket.Models;

public class ServiceItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public string ImageStoragePath { get; set; } = string.Empty;
    public string? ImageFileName { get; set; }
    public string? ImageContentType { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<ItemChatThread> ChatThreads { get; set; } = new List<ItemChatThread>();
}
