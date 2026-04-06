using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamProfileService : ISteamProfileService
{
    private readonly HttpClient _httpClient;
    private readonly SteamApiOptions _options;
    private readonly ILogger<SteamProfileService> _logger;

    public SteamProfileService(
        HttpClient httpClient,
        IOptions<SteamApiOptions> options,
        ILogger<SteamProfileService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SteamProfileSummary?> GetProfileAsync(string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            _logger.LogWarning("Steam profile loading skipped because SteamId is missing.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Steam profile loading skipped for SteamId {SteamId} because SteamApi:ApiKey is missing or empty.", steamId);
            return null;
        }

        var requestUri =
            $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(_options.ApiKey)}&steamids={Uri.EscapeDataString(steamId)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Steam profile API returned non-success status code {StatusCode} for SteamId {SteamId}.",
                    (int)response.StatusCode,
                    steamId);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SteamPlayerSummariesResponse>(
                responseStream,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                cancellationToken: cancellationToken);

            var player = payload?.Response?.Players?.FirstOrDefault();
            if (player is null || string.IsNullOrWhiteSpace(player.SteamId) || string.IsNullOrWhiteSpace(player.PersonaName))
            {
                _logger.LogWarning("Steam profile API returned no usable player summary for SteamId {SteamId}.", steamId);
                return null;
            }

            return new SteamProfileSummary
            {
                SteamId = player.SteamId,
                PersonaName = player.PersonaName,
                AvatarUrl = string.IsNullOrWhiteSpace(player.AvatarFull)
                    ? player.Avatar
                    : player.AvatarFull
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Steam profile API JSON parsing failed for SteamId {SteamId}.", steamId);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Steam profile API request failed unexpectedly for SteamId {SteamId}.", steamId);
            return null;
        }
    }

    private sealed class SteamPlayerSummariesResponse
    {
        [JsonPropertyName("response")]
        public SteamPlayerSummariesPayload? Response { get; set; }
    }

    private sealed class SteamPlayerSummariesPayload
    {
        [JsonPropertyName("players")]
        public List<SteamPlayerSummary>? Players { get; set; }
    }

    private sealed class SteamPlayerSummary
    {
        [JsonPropertyName("steamid")]
        public string SteamId { get; set; } = string.Empty;

        [JsonPropertyName("personaname")]
        public string PersonaName { get; set; } = string.Empty;

        [JsonPropertyName("avatar")]
        public string? Avatar { get; set; }

        [JsonPropertyName("avatarfull")]
        public string? AvatarFull { get; set; }
    }
}
