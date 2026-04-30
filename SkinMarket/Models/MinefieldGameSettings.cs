namespace SkinMarket.Models;

public class MinefieldGameSettings
{
    public Guid Id { get; set; }
    public string GameKey { get; set; } = MinefieldGameSettingsDefaults.GameKey;
    public bool IsEnabled { get; set; } = true;
    public decimal MinimumBet { get; set; } = MinefieldGameSettingsDefaults.MinimumBet;
    public decimal MaximumBet { get; set; } = MinefieldGameSettingsDefaults.MaximumBet;
    public int Rows { get; set; } = MinefieldGameSettingsDefaults.Rows;
    public int Columns { get; set; } = MinefieldGameSettingsDefaults.Columns;
    public int MinesPerLine { get; set; } = MinefieldGameSettingsDefaults.MinesPerLine;
    public decimal ReturnToPlayer { get; set; } = MinefieldGameSettingsDefaults.ReturnToPlayer;
    public bool UseCustomStepSafeChances { get; set; }
    public string StepSafeChancesJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public static class MinefieldGameSettingsDefaults
{
    public const string GameKey = "minefield";
    public const int Rows = 10;
    public const int Columns = 5;
    public const int MinesPerLine = 1;
    public const decimal ReturnToPlayer = 0.95m;
    public const decimal MinimumBet = 0.01m;
    public const decimal MaximumBet = 1000000m;
    public const decimal MinimumSafeChance = 0.01m;
    public const decimal MaximumSafeChance = 1m;
}
