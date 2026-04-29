using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class MinefieldGameService : IMinefieldGameService
{
    private const int Rows = 10;
    private const int Columns = 5;
    private const int MinesPerLine = 1;
    private const decimal ReturnToPlayer = 0.95m;
    private const decimal MinimumBet = 0.01m;
    private const decimal MaximumBet = 1000000m;

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
        var roundedBet = decimal.Round(bet, 2, MidpointRounding.AwayFromZero);
        if (roundedBet < MinimumBet)
        {
            return new MinefieldStartResult { Message = "Enter a bet greater than 0." };
        }

        if (roundedBet > MaximumBet)
        {
            return new MinefieldStartResult { Message = "Bet is too large." };
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
        var multipliers = CreateMultipliers();
        var resultSteps = CreateResultSteps();
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
            Rows = Rows,
            Columns = Columns,
            Result = ToBoolList(resultSteps),
            Multipliers = multipliers
        };
    }

    public async Task<MinefieldStepResult> StepAsync(
        Guid appUserId,
        MinefieldStepRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Row < 0 || request.Row >= Rows || request.Column < 0 || request.Column >= Columns)
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
            IsComplete = session.CurrentStep >= Rows,
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

        if (requestedStep > Rows)
        {
            return new MinefieldClaimResult { Message = "Claim step is invalid." };
        }

        if (!AreStepsSafe(session.ResultSteps, requestedStep))
        {
            var nowLost = DateTime.UtcNow;
            session.Status = MinefieldGameSessionStatus.Lost;
            session.CurrentStep = Math.Min(requestedStep, Rows);
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

    private static List<decimal> CreateMultipliers()
    {
        List<decimal> multipliers = new(Rows);
        var safeChance = (Columns - MinesPerLine) / (decimal)Columns;
        var cumulativeSafeChance = 1m;
        for (var step = 0; step < Rows; step++)
        {
            cumulativeSafeChance *= safeChance;
            var multiplier = decimal.Round(ReturnToPlayer / cumulativeSafeChance, 1, MidpointRounding.AwayFromZero);
            multipliers.Add(multiplier);
        }

        return multipliers;
    }

    private static string CreateResultSteps()
    {
        Span<char> steps = stackalloc char[Rows];
        var safeThreshold = Columns - MinesPerLine;
        for (var i = 0; i < Rows; i++)
        {
            steps[i] = RandomNumberGenerator.GetInt32(Columns) < safeThreshold ? '1' : '0';
        }

        return new string(steps);
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
