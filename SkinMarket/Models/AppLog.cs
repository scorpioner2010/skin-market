namespace SkinMarket.Models;

public class AppLog
{
    public Guid Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? StackTrace { get; set; }
}
