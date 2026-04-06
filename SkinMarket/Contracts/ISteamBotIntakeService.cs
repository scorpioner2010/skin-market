using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamBotIntakeService
{
    Task<BotIntakeResult> CreateIntakeRequestAsync(Guid tradeOperationId, Guid appUserId, CancellationToken cancellationToken = default);
}
