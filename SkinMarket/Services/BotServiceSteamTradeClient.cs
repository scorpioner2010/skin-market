using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Infrastructure;
using SkinMarket.Models;

namespace SkinMarket.Services;

public class BotServiceSteamTradeClient : ISteamTradeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly SteamBotOptions _options;
    private readonly IAppLogService _appLogService;
    private readonly ILogger<BotServiceSteamTradeClient> _logger;

    public BotServiceSteamTradeClient(
        HttpClient httpClient,
        IOptions<SteamBotOptions> options,
        IAppLogService appLogService,
        ILogger<BotServiceSteamTradeClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _appLogService = appLogService;
        _logger = logger;
    }

    public async Task<BotIntakeResult> CreateIntakeTradeAsync(TradeOperation operation, AppUser seller, CancellationToken cancellationToken = default)
    {
        if (!SteamTradeUrlUtility.BelongsToSteamId(seller.TradeUrl, seller.SteamId))
        {
            return new BotIntakeResult
            {
                NewStatus = "Failed",
                Message = "Seller Trade URL does not match the authenticated Steam account."
            };
        }

        var request = new CreateIntakeTradeRequest
        {
            TradeOperationId = operation.Id,
            SellerSteamId = seller.SteamId,
            SellerTradeUrl = seller.TradeUrl!,
            AppId = operation.AppId,
            ContextId = operation.ContextId,
            AssetId = operation.AssetId,
            ClassId = operation.ClassId,
            InstanceId = operation.InstanceId,
            ItemName = operation.ItemName
        };

        await _appLogService.WriteAsync(
            "Info",
            $"Bot intake request started. TradeOperationId={operation.Id}; SteamId={seller.SteamId}; AssetId={operation.AssetId}; AppId={operation.AppId}; ContextId={operation.ContextId}",
            nameof(BotServiceSteamTradeClient),
            cancellationToken: cancellationToken);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/trades/intake", request, SerializerOptions, cancellationToken);
            var payload = await DeserializeAsync<CreateTradeResponse>(response, cancellationToken);
            if (!response.IsSuccessStatusCode || payload is null)
            {
                var message = payload?.Message ?? $"Bot intake request failed with HTTP {(int)response.StatusCode}.";
                await _appLogService.WriteAsync("Error", message, nameof(BotServiceSteamTradeClient), cancellationToken: cancellationToken);
                return new BotIntakeResult
                {
                    NewStatus = "Failed",
                    Message = message
                };
            }

            await _appLogService.WriteAsync(
                payload.Success ? "Info" : "Warning",
                $"Bot intake request finished. TradeOperationId={operation.Id}; Success={payload.Success}; Status={payload.NewStatus}; OfferId={payload.TradeOfferId ?? "<null>"}; Message={payload.Message}",
                nameof(BotServiceSteamTradeClient),
                cancellationToken: cancellationToken);

            return new BotIntakeResult
            {
                Success = payload.Success,
                NewStatus = payload.NewStatus ?? "Failed",
                TradeOfferId = payload.TradeOfferId,
                Message = payload.Message ?? "Bot intake request failed."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bot intake request failed for TradeOperationId {TradeOperationId}.", operation.Id);
            await _appLogService.WriteAsync(
                "Error",
                $"Bot intake request failed unexpectedly. TradeOperationId={operation.Id}",
                nameof(BotServiceSteamTradeClient),
                exception,
                cancellationToken);

            return new BotIntakeResult
            {
                NewStatus = "Failed",
                Message = "Bot intake request failed unexpectedly."
            };
        }
    }

    public async Task<MarketDeliveryResult> CreateDeliveryTradeAsync(
        MarketItem marketItem,
        TradeOperation sourceOperation,
        AppUser buyer,
        CancellationToken cancellationToken = default)
    {
        if (!SteamTradeUrlUtility.BelongsToSteamId(buyer.TradeUrl, buyer.SteamId))
        {
            return new MarketDeliveryResult
            {
                NewStatus = "DeliveryFailed",
                Message = "Buyer Trade URL does not match the authenticated Steam account."
            };
        }

        if (string.IsNullOrWhiteSpace(sourceOperation.BotAssetId) ||
            string.IsNullOrWhiteSpace(sourceOperation.BotClassId) ||
            string.IsNullOrWhiteSpace(sourceOperation.BotInstanceId))
        {
            return new MarketDeliveryResult
            {
                NewStatus = "DeliveryFailed",
                Message = "Bot inventory asset mapping is missing for this market item."
            };
        }

        var request = new CreateDeliveryTradeRequest
        {
            MarketItemId = marketItem.Id,
            BuyerSteamId = buyer.SteamId,
            BuyerTradeUrl = buyer.TradeUrl!,
            AppId = sourceOperation.AppId,
            ContextId = sourceOperation.ContextId,
            AssetId = sourceOperation.BotAssetId,
            ClassId = sourceOperation.BotClassId,
            InstanceId = sourceOperation.BotInstanceId,
            ItemName = marketItem.ItemName
        };

        await _appLogService.WriteAsync(
            "Info",
            $"Bot delivery request started. MarketItemId={marketItem.Id}; BuyerSteamId={buyer.SteamId}; AssetId={request.AssetId}; AppId={request.AppId}; ContextId={request.ContextId}",
            nameof(BotServiceSteamTradeClient),
            cancellationToken: cancellationToken);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/trades/delivery", request, SerializerOptions, cancellationToken);
            var payload = await DeserializeAsync<CreateTradeResponse>(response, cancellationToken);
            if (!response.IsSuccessStatusCode || payload is null)
            {
                var message = payload?.Message ?? $"Bot delivery request failed with HTTP {(int)response.StatusCode}.";
                await _appLogService.WriteAsync("Error", message, nameof(BotServiceSteamTradeClient), cancellationToken: cancellationToken);
                return new MarketDeliveryResult
                {
                    NewStatus = "DeliveryFailed",
                    Message = message
                };
            }

            await _appLogService.WriteAsync(
                payload.Success ? "Info" : "Warning",
                $"Bot delivery request finished. MarketItemId={marketItem.Id}; Success={payload.Success}; Status={payload.NewStatus}; OfferId={payload.TradeOfferId ?? "<null>"}; Message={payload.Message}",
                nameof(BotServiceSteamTradeClient),
                cancellationToken: cancellationToken);

            return new MarketDeliveryResult
            {
                Success = payload.Success,
                NewStatus = payload.NewStatus ?? "DeliveryFailed",
                DeliveryTradeOfferId = payload.TradeOfferId,
                Message = payload.Message ?? "Bot delivery request failed."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bot delivery request failed for MarketItemId {MarketItemId}.", marketItem.Id);
            await _appLogService.WriteAsync(
                "Error",
                $"Bot delivery request failed unexpectedly. MarketItemId={marketItem.Id}",
                nameof(BotServiceSteamTradeClient),
                exception,
                cancellationToken);

            return new MarketDeliveryResult
            {
                NewStatus = "DeliveryFailed",
                Message = "Bot delivery request failed unexpectedly."
            };
        }
    }

    public async Task<IReadOnlyList<SteamTradeOfferStatusResult>> GetOfferStatusesAsync(
        IReadOnlyCollection<SteamTradeOfferStatusRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return Array.Empty<SteamTradeOfferStatusResult>();
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/trades/statuses",
                new GetOfferStatusesRequest { Offers = requests.ToList() },
                SerializerOptions,
                cancellationToken);

            var payload = await DeserializeAsync<GetOfferStatusesResponse>(response, cancellationToken);
            if (!response.IsSuccessStatusCode || payload?.Offers is null)
            {
                var message = payload?.Message ?? $"Bot status polling failed with HTTP {(int)response.StatusCode}.";
                await _appLogService.WriteAsync("Error", message, nameof(BotServiceSteamTradeClient), cancellationToken: cancellationToken);
                return Array.Empty<SteamTradeOfferStatusResult>();
            }

            return payload.Offers;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Bot status polling failed for {Count} offers.", requests.Count);
            await _appLogService.WriteAsync(
                "Error",
                $"Bot status polling failed unexpectedly. Count={requests.Count}",
                nameof(BotServiceSteamTradeClient),
                exception,
                cancellationToken);
            return Array.Empty<SteamTradeOfferStatusResult>();
        }
    }

    private async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(content, SerializerOptions);
    }

    private sealed class CreateIntakeTradeRequest
    {
        public Guid TradeOperationId { get; set; }
        public string SellerSteamId { get; set; } = string.Empty;
        public string SellerTradeUrl { get; set; } = string.Empty;
        public int AppId { get; set; }
        public string ContextId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string ClassId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
    }

    private sealed class CreateDeliveryTradeRequest
    {
        public Guid MarketItemId { get; set; }
        public string BuyerSteamId { get; set; } = string.Empty;
        public string BuyerTradeUrl { get; set; } = string.Empty;
        public int AppId { get; set; }
        public string ContextId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string ClassId { get; set; } = string.Empty;
        public string InstanceId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
    }

    private sealed class CreateTradeResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? TradeOfferId { get; set; }
        public string? NewStatus { get; set; }
    }

    private sealed class GetOfferStatusesRequest
    {
        public List<SteamTradeOfferStatusRequest> Offers { get; set; } = new();
    }

    private sealed class GetOfferStatusesResponse
    {
        public string? Message { get; set; }
        public List<SteamTradeOfferStatusResult>? Offers { get; set; }
    }
}
