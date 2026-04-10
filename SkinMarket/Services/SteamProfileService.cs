using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class SteamProfileService : ISteamProfileService
{
    private const string SteamApiKeyEnvironmentVariable = "STEAM_API_KEY";
    private readonly HttpClient _httpClient;
    private readonly IAppLogService _appLogService;
    private readonly ILogger<SteamProfileService> _logger;

    public SteamProfileService(
        HttpClient httpClient,
        IAppLogService appLogService,
        ILogger<SteamProfileService> logger)
    {
        _httpClient = httpClient;
        _appLogService = appLogService;
        _logger = logger;
    }

    public async Task<SteamProfileSummary?> GetProfileAsync(string steamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            _logger.LogWarning("Steam profile loading skipped because SteamId is missing.");
            return null;
        }

        var apiKey = Environment.GetEnvironmentVariable(SteamApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            const string message = "Steam profile loading skipped because STEAM_API_KEY is missing or empty.";
            _logger.LogWarning("{Message} SteamId {SteamId}.", message, steamId);
            await _appLogService.WriteAsync("Warning", $"{message} SteamId={steamId}", nameof(SteamProfileService), cancellationToken: cancellationToken);
            return null;
        }

        var requestUri =
            $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId)}";

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"Steam profile API returned status {(int)response.StatusCode} for SteamId={steamId}.";
                _logger.LogWarning(
                    "Steam profile API returned non-success status code {StatusCode} for SteamId {SteamId}.",
                    (int)response.StatusCode,
                    steamId);
                await _appLogService.WriteAsync("Warning", message, nameof(SteamProfileService), cancellationToken: cancellationToken);
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
                var message = $"Steam profile API returned no usable player summary for SteamId={steamId}.";
                _logger.LogWarning("Steam profile API returned no usable player summary for SteamId {SteamId}.", steamId);
                await _appLogService.WriteAsync("Warning", message, nameof(SteamProfileService), cancellationToken: cancellationToken);
                return null;
            }

            return new SteamProfileSummary
            {
                SteamId = player.SteamId,
                PersonaName = player.PersonaName,
                AvatarFull = string.IsNullOrWhiteSpace(player.AvatarFull)
                    ? player.Avatar
                    : player.AvatarFull
            };
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "Steam profile API JSON parsing failed for SteamId {SteamId}.", steamId);
            await _appLogService.WriteAsync("Error", $"Steam profile API JSON parsing failed for SteamId={steamId}.", nameof(SteamProfileService), exception, cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Steam profile API request failed unexpectedly for SteamId {SteamId}.", steamId);
            await _appLogService.WriteAsync("Error", $"Steam profile API request failed for SteamId={steamId}.", nameof(SteamProfileService), exception, cancellationToken);
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
