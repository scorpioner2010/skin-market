using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ICreditService
{
    Task<BotIntakeResult> ConfirmReceivedAndCreditAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default);
}
