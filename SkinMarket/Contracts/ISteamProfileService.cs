using SkinMarket.Models;

namespace SkinMarket.Contracts;

public interface ISteamProfileService
{
    Task<SteamProfileSummary?> GetProfileAsync(string steamId, CancellationToken cancellationToken);
}
