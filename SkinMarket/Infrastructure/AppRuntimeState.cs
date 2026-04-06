namespace SkinMarket.Infrastructure;

public class AppRuntimeState
{
    public bool IsDatabaseAvailable { get; init; }
    public bool IsDegradedMode => !IsDatabaseAvailable;
    public string ServiceUnavailableMessage { get; init; } =
        "Database-backed features are temporarily unavailable.";
}
