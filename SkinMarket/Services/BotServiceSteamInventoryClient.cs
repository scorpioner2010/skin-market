using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class BotServiceSteamInventoryClient : ISteamBotInventoryClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly SteamBotOptions _options;
    private readonly IGameCatalog _gameCatalog;
    private readonly IAppLogService _appLogService;
    private readonly ILogger<BotServiceSteamInventoryClient> _logger;

    public BotServiceSteamInventoryClient(
        HttpClient httpClient,
        IOptions<SteamBotOptions> options,
        IGameCatalog gameCatalog,
        IAppLogService appLogService,
        ILogger<BotServiceSteamInventoryClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _gameCatalog = gameCatalog;
        _appLogService = appLogService;
        _logger = logger;
    }

    public async Task<SteamInventoryResultDto> GetInventoryAsync(
        string steamId,
        GameType gameType,
        CancellationToken cancellationToken = default)
    {
        var game = _gameCatalog.Get(gameType);
        var request = new GetInventoryRequest
        {
            SteamId = steamId,
            AppId = game.SteamAppId,
            ContextId = game.SteamContextId.ToString()
        };

        await _appLogService.WriteAsync(
            "Info",
            $"Bot inventory request started. SteamId={steamId}; Game={game.Key}; ServiceUrl={_options.ServiceUrl}",
            nameof(BotServiceSteamInventoryClient),
            cancellationToken: cancellationToken);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/inventory/user",
                request,
                SerializerOptions,
                cancellationToken);
            var payload = await DeserializeAsync<GetInventoryResponse>(response, cancellationToken);
            if (!response.IsSuccessStatusCode || payload is null)
            {
                var message = payload?.Message ?? $"Bot inventory request failed with HTTP {(int)response.StatusCode}.";
                await _appLogService.WriteAsync(
                    "Warning",
                    $"Bot inventory request failed. SteamId={steamId}; Game={game.Key}; Http={(int)response.StatusCode}; Message={message}",
                    nameof(BotServiceSteamInventoryClient),
                    cancellationToken: cancellationToken);
                return new SteamInventoryResultDto
                {
                    ErrorMessage = message
                };
            }

            foreach (var item in payload.Items)
            {
                item.GameType = gameType;
            }

            await _appLogService.WriteAsync(
                payload.Success ? "Info" : "Warning",
                $"Bot inventory request finished. SteamId={steamId}; Game={game.Key}; Success={payload.Success}; ItemCount={payload.Items.Count}; TotalInventoryCount={payload.TotalInventoryCount?.ToString() ?? "<null>"}; Message={payload.Message ?? "<null>"}",
                nameof(BotServiceSteamInventoryClient),
                cancellationToken: cancellationToken);

            return new SteamInventoryResultDto
            {
                IsSuccess = payload.Success,
                ErrorMessage = payload.Success ? null : payload.Message,
                Items = payload.Items
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bot inventory request failed for SteamId {SteamId} and game {GameKey}.", steamId, game.Key);
            await _appLogService.WriteAsync(
                "Error",
                $"Bot inventory request failed unexpectedly. SteamId={steamId}; Game={game.Key}",
                nameof(BotServiceSteamInventoryClient),
                exception,
                cancellationToken);

            return new SteamInventoryResultDto
            {
                ErrorMessage = "Bot inventory request failed unexpectedly."
            };
        }
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(content, SerializerOptions);
    }

    private sealed class GetInventoryRequest
    {
        public string SteamId { get; set; } = string.Empty;
        public int AppId { get; set; }
        public string ContextId { get; set; } = string.Empty;
    }

    private sealed class GetInventoryResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int? TotalInventoryCount { get; set; }
        public List<SteamInventoryItemDto> Items { get; set; } = new();
    }
}
