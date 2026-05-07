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
    public bool WebSessionReady { get; set; }
    public bool TradeManagerReady { get; set; }
    public DateTimeOffset? LastReadyAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastDisconnectReason { get; set; }
    public DateTimeOffset? LastDisconnectAt { get; set; }
    public DateTimeOffset? LastLogonAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessfulLogonAt { get; set; }
    public string? LogonMode { get; set; }
    public string? BotSteamId { get; set; }
    public bool SteamIdConfigured { get; set; }
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
    public string? NotReadyReason { get; set; }
    public string? RecommendedNextCheck { get; set; }
    public int? UptimeSeconds { get; set; }
    public int? ProcessMemoryMb { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public DateTimeOffset? LastRestartDetectedAt { get; set; }
    public string? PreviousErrorBeforeRecovery { get; set; }
    public DateTimeOffset? RecoveredFromErrorAt { get; set; }
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

public class BotServiceLogQuery
{
    public int Limit { get; set; } = 100;
    public string? Level { get; set; }
    public string? Source { get; set; }
    public string? EventType { get; set; }
    public string? TradeOperationId { get; set; }
    public string? OfferId { get; set; }
}

public class BotServiceLogSnapshot
{
    public bool Reachable { get; set; } = true;
    public string? ReachabilityError { get; set; }
    public List<BotServiceLogEntry> Entries { get; set; } = new();
}

public class BotServiceLogEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? TimestampUtc { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? CorrelationId { get; set; }
    public string? TradeOperationId { get; set; }
    public string? OfferId { get; set; }
    public string? ServiceState { get; set; }
}
