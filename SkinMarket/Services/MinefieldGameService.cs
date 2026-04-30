using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MinefieldGameService : IMinefieldGameService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _dbContext;
    private readonly IAppLogService _appLogService;

    public MinefieldGameService(AppDbContext dbContext, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _appLogService = appLogService;
    }

    public async Task<MinefieldGameState> GetStateAsync(Guid appUserId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == appUserId, cancellationToken);
        if (user is null)
        {
            return new MinefieldGameState { Message = "Local user profile was not found." };
        }

        var activeSession = await _dbContext.MinefieldGameSessions
            .AsNoTracking()
            .Where(item => item.AppUserId == appUserId && item.Status == MinefieldGameSessionStatus.Active)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new MinefieldGameState
        {
            Success = true,
            Balance = user.Balance,
            UserName = ResolveUserName(user),
            ActiveSession = activeSession is null
                ? null
                : new MinefieldActiveSession
                {
                    SessionId = activeSession.Id,
                    Bet = activeSession.BetAmount,
                    CurrentStep = activeSession.CurrentStep,
                    Multipliers = ReadMultipliers(activeSession)
                }
        };
    }

    public async Task<MinefieldStartResult> StartAsync(Guid appUserId, decimal bet, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
        {
            return new MinefieldStartResult { Message = "Minefield is currently disabled." };
        }

        var roundedBet = decimal.Round(bet, 2, MidpointRounding.AwayFromZero);
        if (roundedBet < settings.MinimumBet)
        {
            return new MinefieldStartResult { Message = $"Minimum bet is {settings.MinimumBet:0.00}." };
        }

        if (roundedBet > settings.MaximumBet)
        {
            return new MinefieldStartResult { Message = $"Maximum bet is {settings.MaximumBet:0.00}." };
        }

        var user = await _dbContext.AppUsers
            .SingleOrDefaultAsync(item => item.Id == appUserId, cancellationToken);
        if (user is null)
        {
            return new MinefieldStartResult { Message = "Local user profile was not found." };
        }

        if (user.Balance < roundedBet)
        {
            return new MinefieldStartResult { Message = "Not enough balance." };
        }

        var now = DateTime.UtcNow;
        var multipliers = CreateMultipliers(settings);
        var resultSteps = CreateResultSteps(settings);
        user.Balance -= roundedBet;

        _dbContext.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            Amount = -roundedBet,
            Type = "MinefieldBet",
            CreatedAtUtc = now
        });

        var session = new MinefieldGameSession
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            BetAmount = roundedBet,
            Status = MinefieldGameSessionStatus.Active,
            CurrentStep = 0,
            ResultSteps = resultSteps,
            MultipliersJson = JsonSerializer.Serialize(multipliers, JsonOptions),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.MinefieldGameSessions.Add(session);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Info",
            $"Minefield session started. SessionId={session.Id}; AppUserId={appUserId}; Bet={roundedBet:0.00}; Balance={user.Balance:0.00}",
            nameof(MinefieldGameService),
            cancellationToken: cancellationToken);

        return new MinefieldStartResult
        {
            Success = true,
            SessionId = session.Id,
            Balance = user.Balance,
            Bet = roundedBet,
            Rows = settings.Rows,
            Columns = settings.Columns,
            Result = ToBoolList(resultSteps),
            Multipliers = multipliers
        };
    }

    public async Task<MinefieldStepResult> StepAsync(
        Guid appUserId,
        MinefieldStepRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Row < 0 ||
            request.Row >= MinefieldGameSettingsDefaults.Rows ||
            request.Column < 0 ||
            request.Column >= MinefieldGameSettingsDefaults.Columns)
        {
            return new MinefieldStepResult { Message = "Selected cell is invalid." };
        }

        var session = await _dbContext.MinefieldGameSessions
            .SingleOrDefaultAsync(
                item => item.Id == request.SessionId &&
                        item.AppUserId == appUserId &&
                        item.Status == MinefieldGameSessionStatus.Active,
                cancellationToken);
        if (session is null)
        {
            return new MinefieldStepResult { Message = "Active game was not found." };
        }

        if (request.Row != session.CurrentStep)
        {
            return new MinefieldStepResult { Message = "Selected row is not active." };
        }

        var userBalance = await _dbContext.AppUsers
            .Where(item => item.Id == appUserId)
            .Select(item => item.Balance)
            .SingleAsync(cancellationToken);

        var isSafe = IsSafeStep(session.ResultSteps, session.CurrentStep);
        var now = DateTime.UtcNow;
        if (!isSafe)
        {
            session.Status = MinefieldGameSessionStatus.Lost;
            session.EndedAtUtc = now;
            session.UpdatedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _appLogService.WriteAsync(
                "Info",
                $"Minefield session lost. SessionId={session.Id}; AppUserId={appUserId}; Step={session.CurrentStep}; Bet={session.BetAmount:0.00}",
                nameof(MinefieldGameService),
                cancellationToken: cancellationToken);

            return new MinefieldStepResult
            {
                Success = true,
                IsMine = true,
                CurrentStep = session.CurrentStep,
                Balance = userBalance
            };
        }

        session.CurrentStep++;
        session.UpdatedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new MinefieldStepResult
        {
            Success = true,
            IsMine = false,
            IsComplete = session.CurrentStep >= session.ResultSteps.Length,
            CurrentStep = session.CurrentStep,
            Balance = userBalance
        };
    }

    public async Task<MinefieldClaimResult> ClaimAsync(
        Guid appUserId,
        MinefieldClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.MinefieldGameSessions
            .SingleOrDefaultAsync(
                item => item.Id == request.SessionId &&
                        item.AppUserId == appUserId &&
                        item.Status == MinefieldGameSessionStatus.Active,
                cancellationToken);
        if (session is null)
        {
            return new MinefieldClaimResult { Message = "Active game was not found." };
        }

        var requestedStep = request.CurrentStep > 0 ? request.CurrentStep : session.CurrentStep;
        if (requestedStep <= 0)
        {
            return new MinefieldClaimResult { Message = "Open at least one safe row before claiming." };
        }

        if (requestedStep > session.ResultSteps.Length)
        {
            return new MinefieldClaimResult { Message = "Claim step is invalid." };
        }

        if (!AreStepsSafe(session.ResultSteps, requestedStep))
        {
            var nowLost = DateTime.UtcNow;
            session.Status = MinefieldGameSessionStatus.Lost;
            session.CurrentStep = Math.Min(requestedStep, session.ResultSteps.Length);
            session.EndedAtUtc = nowLost;
            session.UpdatedAtUtc = nowLost;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new MinefieldClaimResult { Message = "Cannot claim after a mine step." };
        }

        var user = await _dbContext.AppUsers
            .SingleOrDefaultAsync(item => item.Id == appUserId, cancellationToken);
        if (user is null)
        {
            return new MinefieldClaimResult { Message = "Local user profile was not found." };
        }

        var multipliers = ReadMultipliers(session);
        var multiplier = multipliers[Math.Min(requestedStep, multipliers.Count) - 1];
        var payout = decimal.Round(session.BetAmount * multiplier, 2, MidpointRounding.AwayFromZero);
        var now = DateTime.UtcNow;

        user.Balance += payout;
        session.Status = MinefieldGameSessionStatus.Claimed;
        session.CurrentStep = requestedStep;
        session.PayoutAmount = payout;
        session.EndedAtUtc = now;
        session.UpdatedAtUtc = now;

        _dbContext.BalanceTransactions.Add(new BalanceTransaction
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            Amount = payout,
            Type = "MinefieldPayout",
            CreatedAtUtc = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _appLogService.WriteAsync(
            "Info",
            $"Minefield session claimed. SessionId={session.Id}; AppUserId={appUserId}; Step={requestedStep}; Bet={session.BetAmount:0.00}; Payout={payout:0.00}; Balance={user.Balance:0.00}",
            nameof(MinefieldGameService),
            cancellationToken: cancellationToken);

        return new MinefieldClaimResult
        {
            Success = true,
            Balance = user.Balance,
            Payout = payout
        };
    }

    private async Task<MinefieldGameSettings> GetSettingsAsync(CancellationToken cancellationToken)
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
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _dbContext.MinefieldGameSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static void NormalizeSettings(MinefieldGameSettings settings)
    {
        settings.Rows = MinefieldGameSettingsDefaults.Rows;
        settings.Columns = MinefieldGameSettingsDefaults.Columns;
        settings.MinimumBet = Math.Max(MinefieldGameSettingsDefaults.MinimumBet, decimal.Round(settings.MinimumBet, 2, MidpointRounding.AwayFromZero));
        settings.MaximumBet = Math.Max(settings.MinimumBet, decimal.Round(settings.MaximumBet, 2, MidpointRounding.AwayFromZero));
        settings.MinesPerLine = Math.Clamp(settings.MinesPerLine, 1, settings.Columns - 1);
        settings.ReturnToPlayer = Math.Clamp(settings.ReturnToPlayer, 0.01m, 1m);
    }

    private static List<decimal> CreateMultipliers(MinefieldGameSettings settings)
    {
        List<decimal> multipliers = new(settings.Rows);
        var customSafeChances = ReadStepSafeChances(settings);
        var cumulativeSafeChance = 1m;
        for (var step = 0; step < settings.Rows; step++)
        {
            var safeChance = GetStepSafeChance(settings, step, customSafeChances);
            cumulativeSafeChance *= safeChance;
            var multiplier = decimal.Round(settings.ReturnToPlayer / cumulativeSafeChance, 1, MidpointRounding.AwayFromZero);
            multipliers.Add(multiplier);
        }

        return multipliers;
    }

    private static string CreateResultSteps(MinefieldGameSettings settings)
    {
        var steps = new char[settings.Rows];
        var customSafeChances = ReadStepSafeChances(settings);
        for (var i = 0; i < settings.Rows; i++)
        {
            steps[i] = RollSafe(GetStepSafeChance(settings, i, customSafeChances)) ? '1' : '0';
        }

        return new string(steps);
    }

    private static bool RollSafe(decimal safeChance)
    {
        var threshold = (int)decimal.Round(
            ClampSafeChance(safeChance) * 10000m,
            0,
            MidpointRounding.AwayFromZero);
        return RandomNumberGenerator.GetInt32(10000) < threshold;
    }

    private static decimal GetStepSafeChance(
        MinefieldGameSettings settings,
        int step,
        IReadOnlyList<decimal> customSafeChances)
    {
        if (settings.UseCustomStepSafeChances &&
            step >= 0 &&
            step < customSafeChances.Count)
        {
            return ClampSafeChance(customSafeChances[step]);
        }

        return GetDefaultSafeChance(settings);
    }

    private static decimal GetDefaultSafeChance(MinefieldGameSettings settings)
    {
        return ClampSafeChance((settings.Columns - settings.MinesPerLine) / (decimal)settings.Columns);
    }

    private static decimal ClampSafeChance(decimal safeChance)
    {
        return Math.Clamp(
            safeChance,
            MinefieldGameSettingsDefaults.MinimumSafeChance,
            MinefieldGameSettingsDefaults.MaximumSafeChance);
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

    private static bool IsSafeStep(string resultSteps, int step)
    {
        return step >= 0 && step < resultSteps.Length && resultSteps[step] == '1';
    }

    private static bool AreStepsSafe(string resultSteps, int count)
    {
        if (count < 0 || count > resultSteps.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!IsSafeStep(resultSteps, i))
            {
                return false;
            }
        }

        return true;
    }

    private static List<bool> ToBoolList(string resultSteps)
    {
        return resultSteps.Select(item => item == '1').ToList();
    }

    private static List<decimal> ReadMultipliers(MinefieldGameSession session)
    {
        return JsonSerializer.Deserialize<List<decimal>>(session.MultipliersJson, JsonOptions) ?? new List<decimal>();
    }

    private static string ResolveUserName(AppUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.PersonaName))
        {
            return user.PersonaName;
        }

        return string.IsNullOrWhiteSpace(user.DisplayName) ? "Player" : user.DisplayName;
    }
}
