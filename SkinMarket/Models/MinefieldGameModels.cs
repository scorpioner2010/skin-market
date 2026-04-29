namespace SkinMarket.Models;

public sealed class MinefieldStartRequest
{
    public decimal Bet { get; set; }
}

public sealed class MinefieldStepRequest
{
    public Guid SessionId { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
}

public sealed class MinefieldClaimRequest
{
    public Guid SessionId { get; set; }
    public int CurrentStep { get; set; }
}

public sealed class MinefieldGameState
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public string UserName { get; init; } = string.Empty;
    public MinefieldActiveSession? ActiveSession { get; init; }
}

public sealed class MinefieldActiveSession
{
    public Guid SessionId { get; init; }
    public decimal Bet { get; init; }
    public int CurrentStep { get; init; }
    public List<decimal> Multipliers { get; init; } = new();
}

public sealed class MinefieldStartResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? SessionId { get; init; }
    public decimal Balance { get; init; }
    public decimal Bet { get; init; }
    public int Rows { get; init; }
    public int Columns { get; init; }
    public List<bool> Result { get; init; } = new();
    public List<decimal> Multipliers { get; init; } = new();
}

public sealed class MinefieldStepResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsMine { get; init; }
    public bool IsComplete { get; init; }
    public int CurrentStep { get; init; }
    public decimal Balance { get; init; }
}

public sealed class MinefieldClaimResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public decimal Payout { get; init; }
}
