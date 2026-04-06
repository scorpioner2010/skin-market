namespace SkinMarket.Contracts;

public interface ISteamOpenIdService
{
    string BuildLoginUrl(string returnToUrl);
    Task<string?> ValidateAndExtractSteamIdAsync(IQueryCollection query, CancellationToken cancellationToken = default);
}
