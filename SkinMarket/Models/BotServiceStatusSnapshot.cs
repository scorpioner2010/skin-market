namespace SkinMarket.Models;

public class BotServiceStatusSnapshot
{
    public bool Reachable { get; set; }
    public string Status { get; set; } = "unreachable";
    public string? ReachabilityError { get; set; }
    public BotServiceStatusDetails Bot { get; set; } = new();

    public bool IsActive => Reachable && Bot.Active;
}

public class BotServiceStatusDetails
{
    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public bool Ready { get; set; }
    public bool LoggedOn { get; set; }
    public DateTimeOffset? LastReadyAt { get; set; }
    public string? BotSteamId { get; set; }
    public bool UsernameConfigured { get; set; }
    public bool IdentitySecretConfigured { get; set; }
    public bool SharedSecretConfigured { get; set; }
    public bool SteamApiKeyConfigured { get; set; }
    public string? LastError { get; set; }
    public string? ServiceState { get; set; }
    public string? ServiceStateDescription { get; set; }
    public DateTimeOffset? StateUpdatedAt { get; set; }
    public int ActiveActivityCount { get; set; }
    public List<BotServiceActivitySnapshot> ActiveActivities { get; set; } = new();
    public BotServiceActivitySnapshot? LastCompletedActivity { get; set; }
    public List<BotServiceIssueSnapshot> RecentIssues { get; set; } = new();
}

public class BotServiceActivitySnapshot
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
    public Dictionary<string, string> Meta { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class BotServiceIssueSnapshot
{
    public DateTimeOffset? Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Details { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
