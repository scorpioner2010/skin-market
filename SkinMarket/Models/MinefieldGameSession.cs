namespace SkinMarket.Models;

public class MinefieldGameSession
{
    public Guid Id { get; set; }
    public Guid AppUserId { get; set; }
    public decimal BetAmount { get; set; }
    public string Status { get; set; } = MinefieldGameSessionStatus.Active;
    public int CurrentStep { get; set; }
    public string ResultSteps { get; set; } = string.Empty;
    public string MultipliersJson { get; set; } = string.Empty;
    public decimal? PayoutAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }

    public AppUser? AppUser { get; set; }
}

public static class MinefieldGameSessionStatus
{
    public const string Active = "Active";
    public const string Lost = "Lost";
    public const string Claimed = "Claimed";
}
