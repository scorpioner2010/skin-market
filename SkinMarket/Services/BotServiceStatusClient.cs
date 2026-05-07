using System.Text.Json;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class BotServiceStatusClient : IBotServiceStatusClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly SteamBotOptions _options;
    private readonly ILogger<BotServiceStatusClient> _logger;

    public BotServiceStatusClient(
        HttpClient httpClient,
        IOptions<SteamBotOptions> options,
        ILogger<BotServiceStatusClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BotServiceStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/healthz", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BotServiceStatusSnapshot
                {
                    Reachable = false,
                    Status = $"http_{(int)response.StatusCode}",
                    ReachabilityError = string.IsNullOrWhiteSpace(content)
                        ? $"Bot status request failed with HTTP {(int)response.StatusCode}."
                        : content
                };
            }

            var payload = string.IsNullOrWhiteSpace(content)
                ? null
                : JsonSerializer.Deserialize<BotServiceStatusSnapshot>(content, SerializerOptions);

            if (payload is null)
            {
                return new BotServiceStatusSnapshot
                {
                    Reachable = false,
                    Status = "invalid_payload",
                    ReachabilityError = "Bot status payload was empty."
                };
            }

            payload.Reachable = true;
            if (string.IsNullOrWhiteSpace(payload.Status))
            {
                payload.Status = "ok";
            }

            return payload;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to read Steam bot status from {ServiceUrl}.",
                _options.ServiceUrl);

            return new BotServiceStatusSnapshot
            {
                Reachable = false,
                Status = "unreachable",
                ReachabilityError = exception.Message
            };
        }
    }

    public async Task<BotServiceLogSnapshot> GetLogsAsync(BotServiceLogQuery query, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = BuildLogsPath(query);
            using var response = await _httpClient.GetAsync(path, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new BotServiceLogSnapshot
                {
                    Reachable = false,
                    ReachabilityError = string.IsNullOrWhiteSpace(content)
                        ? $"Bot log request failed with HTTP {(int)response.StatusCode}."
                        : content
                };
            }

            var payload = string.IsNullOrWhiteSpace(content)
                ? null
                : JsonSerializer.Deserialize<BotServiceLogSnapshot>(content, SerializerOptions);

            return payload ?? new BotServiceLogSnapshot();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to read Steam bot logs from {ServiceUrl}.",
                _options.ServiceUrl);

            return new BotServiceLogSnapshot
            {
                Reachable = false,
                ReachabilityError = exception.Message
            };
        }
    }

    private static string BuildLogsPath(BotServiceLogQuery query)
    {
        var parts = new List<string>
        {
            $"limit={Math.Min(Math.Max(query.Limit, 1), 500)}"
        };

        Add("level", query.Level);
        Add("source", query.Source);
        Add("eventType", query.EventType);
        Add("tradeOperationId", query.TradeOperationId);
        Add("offerId", query.OfferId);

        return $"/api/bot/logs?{string.Join("&", parts)}";

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value.Trim())}");
            }
        }
    }
}
