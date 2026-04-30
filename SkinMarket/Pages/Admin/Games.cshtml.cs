using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class GamesModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _dbContext;
    private readonly IAppLogService _appLogService;

    public GamesModel(AppDbContext dbContext, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _appLogService = appLogService;
    }

    public IReadOnlyList<AdminGameListItem> Games { get; private set; } = [];
    public AdminGameListItem? SelectedGame { get; private set; }
    public List<MinefieldStepPreviewItem> MinefieldStepPreview { get; private set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public MinefieldSettingsInputModel MinefieldInput { get; set; } = new();

    public async Task OnGetAsync(string? gameKey, CancellationToken cancellationToken)
    {
        await LoadPageAsync(gameKey, cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveMinefieldAsync(CancellationToken cancellationToken)
    {
        LoadGames(MinefieldGameSettingsDefaults.GameKey);
        NormalizeStepChanceInputs();
        NormalizeStepMultiplierInputs();
        ValidateMinefieldInput();
        MinefieldStepPreview = CreateMinefieldStepPreview(MinefieldInput);

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Minefield settings are invalid.";
            return Page();
        }

        var settings = await GetOrCreateMinefieldSettingsAsync(cancellationToken);
        settings.IsEnabled = MinefieldInput.IsEnabled;
        settings.MinimumBet = decimal.Round(MinefieldInput.MinimumBet, 2, MidpointRounding.AwayFromZero);
        settings.MaximumBet = decimal.Round(MinefieldInput.MaximumBet, 2, MidpointRounding.AwayFromZero);
        settings.Rows = MinefieldGameSettingsDefaults.Rows;
        settings.Columns = MinefieldGameSettingsDefaults.Columns;
        settings.MinesPerLine = MinefieldInput.MinesPerLine;
        settings.ReturnToPlayer = decimal.Round(MinefieldInput.ReturnToPlayerPercent / 100m, 4, MidpointRounding.AwayFromZero);
        settings.UseCustomStepSafeChances = MinefieldInput.UseCustomStepSafeChances;
        settings.StepSafeChancesJson = JsonSerializer.Serialize(
            MinefieldInput.StepSafeChancePercents
                .Take(MinefieldGameSettingsDefaults.Rows)
                .Select(chance => decimal.Round(chance / 100m, 4, MidpointRounding.AwayFromZero))
                .ToList(),
            JsonOptions);
        settings.UseCustomStepMultipliers = MinefieldInput.UseCustomStepMultipliers;
        settings.StepMultipliersJson = JsonSerializer.Serialize(
            MinefieldInput.StepMultipliers
                .Take(MinefieldGameSettingsDefaults.Rows)
                .Select(multiplier => decimal.Round(multiplier, 4, MidpointRounding.AwayFromZero))
                .ToList(),
            JsonOptions);
        settings.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _appLogService.WriteAsync(
            "Warning",
            $"Admin updated Minefield settings. Enabled={settings.IsEnabled}; MinBet={settings.MinimumBet:0.00}; MaxBet={settings.MaximumBet:0.00}; MinesPerLine={settings.MinesPerLine}; RTP={settings.ReturnToPlayer:0.####}; CustomChances={settings.UseCustomStepSafeChances}; CustomMultipliers={settings.UseCustomStepMultipliers}",
            nameof(GamesModel),
            cancellationToken: cancellationToken);

        SuccessMessage = "Minefield settings saved.";
        return RedirectToPage(new { gameKey = MinefieldGameSettingsDefaults.GameKey });
    }

    private async Task LoadPageAsync(string? gameKey, CancellationToken cancellationToken)
    {
        LoadGames(gameKey);

        if (SelectedGame?.Key != MinefieldGameSettingsDefaults.GameKey)
        {
            return;
        }

        var settings = await GetOrCreateMinefieldSettingsAsync(cancellationToken);
        MinefieldInput = CreateMinefieldInput(settings);
        MinefieldStepPreview = CreateMinefieldStepPreview(MinefieldInput);
    }

    private void LoadGames(string? gameKey)
    {
        Games =
        [
            new AdminGameListItem(MinefieldGameSettingsDefaults.GameKey, "Minefield", "Configurable")
        ];

        if (!string.IsNullOrWhiteSpace(gameKey))
        {
            SelectedGame = Games.FirstOrDefault(game =>
                string.Equals(game.Key, gameKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task<MinefieldGameSettings> GetOrCreateMinefieldSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.MinefieldGameSettings
            .SingleOrDefaultAsync(
                item => item.GameKey == MinefieldGameSettingsDefaults.GameKey,
                cancellationToken);
        if (settings is not null)
        {
            NormalizeSettings(settings);
            return settings;
        }

        var now = DateTime.UtcNow;
        settings = new MinefieldGameSettings
        {
            Id = Guid.NewGuid(),
            GameKey = MinefieldGameSettingsDefaults.GameKey,
            IsEnabled = true,
            MinimumBet = MinefieldGameSettingsDefaults.MinimumBet,
            MaximumBet = MinefieldGameSettingsDefaults.MaximumBet,
            Rows = MinefieldGameSettingsDefaults.Rows,
            Columns = MinefieldGameSettingsDefaults.Columns,
            MinesPerLine = MinefieldGameSettingsDefaults.MinesPerLine,
            ReturnToPlayer = MinefieldGameSettingsDefaults.ReturnToPlayer,
            UseCustomStepSafeChances = false,
            StepSafeChancesJson = string.Empty,
            UseCustomStepMultipliers = false,
            StepMultipliersJson = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.MinefieldGameSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static MinefieldSettingsInputModel CreateMinefieldInput(MinefieldGameSettings settings)
    {
        NormalizeSettings(settings);
        var defaultSafeChance = GetDefaultSafeChancePercent(settings.MinesPerLine);
        var customSafeChances = ReadStepSafeChances(settings)
            .Select(chance => decimal.Round(ClampSafeChance(chance) * 100m, 2, MidpointRounding.AwayFromZero))
            .ToList();

        while (customSafeChances.Count < MinefieldGameSettingsDefaults.Rows)
        {
            customSafeChances.Add(defaultSafeChance);
        }

        if (customSafeChances.Count > MinefieldGameSettingsDefaults.Rows)
        {
            customSafeChances = customSafeChances
                .Take(MinefieldGameSettingsDefaults.Rows)
                .ToList();
        }

        var input = new MinefieldSettingsInputModel
        {
            IsEnabled = settings.IsEnabled,
            MinimumBet = settings.MinimumBet,
            MaximumBet = settings.MaximumBet,
            MinesPerLine = settings.MinesPerLine,
            ReturnToPlayerPercent = decimal.Round(settings.ReturnToPlayer * 100m, 2, MidpointRounding.AwayFromZero),
            UseCustomStepSafeChances = settings.UseCustomStepSafeChances,
            StepSafeChancePercents = customSafeChances,
            UseCustomStepMultipliers = settings.UseCustomStepMultipliers,
            StepMultipliers = ReadStepMultipliers(settings)
                .Select(multiplier => decimal.Round(ClampMultiplier(multiplier), 4, MidpointRounding.AwayFromZero))
                .ToList()
        };

        NormalizeStepMultiplierList(input);
        return input;
    }

    private void NormalizeStepChanceInputs()
    {
        if (MinefieldInput.StepSafeChancePercents is null)
        {
            MinefieldInput.StepSafeChancePercents = new List<decimal>();
        }

        var defaultSafeChance = GetDefaultSafeChancePercent(MinefieldInput.MinesPerLine);
        while (MinefieldInput.StepSafeChancePercents.Count < MinefieldGameSettingsDefaults.Rows)
        {
            MinefieldInput.StepSafeChancePercents.Add(defaultSafeChance);
        }

        if (MinefieldInput.StepSafeChancePercents.Count > MinefieldGameSettingsDefaults.Rows)
        {
            MinefieldInput.StepSafeChancePercents = MinefieldInput.StepSafeChancePercents
                .Take(MinefieldGameSettingsDefaults.Rows)
                .ToList();
        }
    }

    private void NormalizeStepMultiplierInputs()
    {
        NormalizeStepMultiplierList(MinefieldInput);
    }

    private static void NormalizeStepMultiplierList(MinefieldSettingsInputModel input)
    {
        if (input.StepMultipliers is null)
        {
            input.StepMultipliers = new List<decimal>();
        }

        var defaultMultipliers = CreateAutomaticStepMultipliers(input);
        while (input.StepMultipliers.Count < MinefieldGameSettingsDefaults.Rows)
        {
            input.StepMultipliers.Add(defaultMultipliers[input.StepMultipliers.Count]);
        }

        if (input.StepMultipliers.Count > MinefieldGameSettingsDefaults.Rows)
        {
            input.StepMultipliers = input.StepMultipliers
                .Take(MinefieldGameSettingsDefaults.Rows)
                .ToList();
        }
    }

    private void ValidateMinefieldInput()
    {
        if (MinefieldInput.MaximumBet < MinefieldInput.MinimumBet)
        {
            ModelState.AddModelError("MinefieldInput.MaximumBet", "Maximum bet must be greater than or equal to minimum bet.");
        }

        for (var i = 0; i < MinefieldInput.StepSafeChancePercents.Count; i++)
        {
            var chance = MinefieldInput.StepSafeChancePercents[i];
            if (chance < 1m || chance > 100m)
            {
                ModelState.AddModelError(
                    $"MinefieldInput.StepSafeChancePercents[{i}]",
                    "Safe chance must be between 1 and 100 percent.");
            }
        }

        for (var i = 0; i < MinefieldInput.StepMultipliers.Count; i++)
        {
            var multiplier = MinefieldInput.StepMultipliers[i];
            if (multiplier < MinefieldGameSettingsDefaults.MinimumMultiplier ||
                multiplier > MinefieldGameSettingsDefaults.MaximumMultiplier)
            {
                ModelState.AddModelError(
                    $"MinefieldInput.StepMultipliers[{i}]",
                    $"Multiplier must be between {MinefieldGameSettingsDefaults.MinimumMultiplier:0.0} and {MinefieldGameSettingsDefaults.MaximumMultiplier:0.##}.");
            }
        }
    }

    private static List<MinefieldStepPreviewItem> CreateMinefieldStepPreview(MinefieldSettingsInputModel input)
    {
        var preview = new List<MinefieldStepPreviewItem>(MinefieldGameSettingsDefaults.Rows);
        var automaticMultipliers = CreateAutomaticStepMultipliers(input);

        for (var i = 0; i < MinefieldGameSettingsDefaults.Rows; i++)
        {
            var safeChance = GetInputStepSafeChance(input, i);
            var multiplier = input.UseCustomStepMultipliers && i < input.StepMultipliers.Count
                ? ClampMultiplier(input.StepMultipliers[i])
                : automaticMultipliers[i];
            preview.Add(new MinefieldStepPreviewItem(
                i + 1,
                decimal.Round(safeChance * 100m, 2, MidpointRounding.AwayFromZero),
                decimal.Round((1m - safeChance) * 100m, 2, MidpointRounding.AwayFromZero),
                decimal.Round(multiplier, 4, MidpointRounding.AwayFromZero)));
        }

        return preview;
    }

    private static List<decimal> CreateAutomaticStepMultipliers(MinefieldSettingsInputModel input)
    {
        var multipliers = new List<decimal>(MinefieldGameSettingsDefaults.Rows);
        var returnToPlayer = Math.Clamp(input.ReturnToPlayerPercent / 100m, 0.01m, 1m);
        var cumulativeSafeChance = 1m;

        for (var i = 0; i < MinefieldGameSettingsDefaults.Rows; i++)
        {
            cumulativeSafeChance *= GetInputStepSafeChance(input, i);
            multipliers.Add(decimal.Round(returnToPlayer / cumulativeSafeChance, 1, MidpointRounding.AwayFromZero));
        }

        return multipliers;
    }

    private static decimal GetInputStepSafeChance(MinefieldSettingsInputModel input, int step)
    {
        var defaultSafeChance = GetDefaultSafeChancePercent(input.MinesPerLine) / 100m;
        return input.UseCustomStepSafeChances && step >= 0 && step < input.StepSafeChancePercents.Count
            ? Math.Clamp(input.StepSafeChancePercents[step] / 100m, 0.01m, 1m)
            : defaultSafeChance;
    }

    private static void NormalizeSettings(MinefieldGameSettings settings)
    {
        settings.Rows = MinefieldGameSettingsDefaults.Rows;
        settings.Columns = MinefieldGameSettingsDefaults.Columns;
        settings.MinimumBet = Math.Max(MinefieldGameSettingsDefaults.MinimumBet, decimal.Round(settings.MinimumBet, 2, MidpointRounding.AwayFromZero));
        settings.MaximumBet = Math.Max(settings.MinimumBet, decimal.Round(settings.MaximumBet, 2, MidpointRounding.AwayFromZero));
        settings.MinesPerLine = Math.Clamp(settings.MinesPerLine, 1, MinefieldGameSettingsDefaults.Columns - 1);
        settings.ReturnToPlayer = Math.Clamp(settings.ReturnToPlayer, 0.01m, 1m);
    }

    private static decimal GetDefaultSafeChancePercent(int minesPerLine)
    {
        var normalizedMines = Math.Clamp(minesPerLine, 1, MinefieldGameSettingsDefaults.Columns - 1);
        return decimal.Round(
            (MinefieldGameSettingsDefaults.Columns - normalizedMines) / (decimal)MinefieldGameSettingsDefaults.Columns * 100m,
            2,
            MidpointRounding.AwayFromZero);
    }

    private static decimal ClampSafeChance(decimal chance)
    {
        return Math.Clamp(
            chance,
            MinefieldGameSettingsDefaults.MinimumSafeChance,
            MinefieldGameSettingsDefaults.MaximumSafeChance);
    }

    private static decimal ClampMultiplier(decimal multiplier)
    {
        return Math.Clamp(
            multiplier,
            MinefieldGameSettingsDefaults.MinimumMultiplier,
            MinefieldGameSettingsDefaults.MaximumMultiplier);
    }

    private static List<decimal> ReadStepSafeChances(MinefieldGameSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StepSafeChancesJson))
        {
            return new List<decimal>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<decimal>>(settings.StepSafeChancesJson, JsonOptions)
                   ?? new List<decimal>();
        }
        catch (JsonException)
        {
            return new List<decimal>();
        }
    }

    private static List<decimal> ReadStepMultipliers(MinefieldGameSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StepMultipliersJson))
        {
            return new List<decimal>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<decimal>>(settings.StepMultipliersJson, JsonOptions)
                   ?? new List<decimal>();
        }
        catch (JsonException)
        {
            return new List<decimal>();
        }
    }

    public sealed record AdminGameListItem(string Key, string Name, string Status);

    public sealed record MinefieldStepPreviewItem(
        int Step,
        decimal SafeChancePercent,
        decimal MineChancePercent,
        decimal Multiplier);

    public sealed class MinefieldSettingsInputModel
    {
        public bool IsEnabled { get; set; } = true;

        [Range(typeof(decimal), "0.01", "1000000")]
        public decimal MinimumBet { get; set; } = MinefieldGameSettingsDefaults.MinimumBet;

        [Range(typeof(decimal), "0.01", "1000000")]
        public decimal MaximumBet { get; set; } = MinefieldGameSettingsDefaults.MaximumBet;

        [Range(1, MinefieldGameSettingsDefaults.Columns - 1)]
        public int MinesPerLine { get; set; } = MinefieldGameSettingsDefaults.MinesPerLine;

        [Range(typeof(decimal), "1", "100")]
        public decimal ReturnToPlayerPercent { get; set; } = MinefieldGameSettingsDefaults.ReturnToPlayer * 100m;

        public bool UseCustomStepSafeChances { get; set; }
        public List<decimal> StepSafeChancePercents { get; set; } = new();
        public bool UseCustomStepMultipliers { get; set; }
        public List<decimal> StepMultipliers { get; set; } = new();
    }
}
