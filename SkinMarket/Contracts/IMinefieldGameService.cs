using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface IMinefieldGameService
{
    Task<MinefieldGameState> GetStateAsync(Guid appUserId, CancellationToken cancellationToken = default);
    Task<MinefieldStartResult> StartAsync(Guid appUserId, decimal bet, CancellationToken cancellationToken = default);
    Task<MinefieldStepResult> StepAsync(Guid appUserId, MinefieldStepRequest request, CancellationToken cancellationToken = default);
    Task<MinefieldClaimResult> ClaimAsync(Guid appUserId, MinefieldClaimRequest request, CancellationToken cancellationToken = default);
}
