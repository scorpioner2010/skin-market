using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using SkinMarket.Contracts;

namespace SkinMarket.Services;

public class SteamOpenIdService : ISteamOpenIdService
{
    private const string OpenIdEndpoint = "https://steamcommunity.com/openid/login";
    private const string OpenIdNamespace = "http://specs.openid.net/auth/2.0";
    private readonly HttpClient _httpClient;

    public SteamOpenIdService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string BuildLoginUrl(string returnToUrl)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["openid.ns"] = OpenIdNamespace,
            ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = returnToUrl,
            ["openid.realm"] = GetRealm(returnToUrl),
            ["openid.identity"] = $"{OpenIdNamespace}/identifier_select",
            ["openid.claimed_id"] = $"{OpenIdNamespace}/identifier_select"
        };

        return QueryHelpers.AddQueryString(OpenIdEndpoint, parameters);
    }

    public async Task<string?> ValidateAndExtractSteamIdAsync(IQueryCollection query, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(query["openid.mode"], "id_res", StringComparison.Ordinal))
        {
            return null;
        }

        var validationParameters = query
            .Where(pair => pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

        validationParameters["openid.mode"] = "check_authentication";

        using var content = new FormUrlEncodedContent(validationParameters);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await _httpClient.PostAsync(OpenIdEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!body.Contains("is_valid:true", StringComparison.Ordinal))
        {
            return null;
        }

        var claimedId = query["openid.claimed_id"].ToString();
        return string.IsNullOrWhiteSpace(claimedId) ? null : TryExtractSteamId(claimedId);
    }

    private static string GetRealm(string returnToUrl)
    {
        var uri = new Uri(returnToUrl);
        var builder = new StringBuilder();
        builder.Append(uri.Scheme);
        builder.Append("://");
        builder.Append(uri.Authority);
        return builder.ToString();
    }

    private static string? TryExtractSteamId(string claimedId)
    {
        const string prefix = "https://steamcommunity.com/openid/id/";

        if (!claimedId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var steamId = claimedId[prefix.Length..];
        return ulong.TryParse(steamId, out _) ? steamId : null;
    }
}
