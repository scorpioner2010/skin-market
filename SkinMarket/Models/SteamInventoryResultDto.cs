namespace SkinMarket.Models;

public class SteamInventoryResultDto
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public List<SteamInventoryItemDto> Items { get; set; } = new();
}
