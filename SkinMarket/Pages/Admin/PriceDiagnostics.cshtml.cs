using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class PriceDiagnosticsModel : PageModel
{
    private readonly IGameCatalog _gameCatalog;
    private readonly IPriceDiagnosticLogService _priceDiagnosticLogService;

    public PriceDiagnosticsModel(
        IGameCatalog gameCatalog,
        IPriceDiagnosticLogService priceDiagnosticLogService)
    {
        _gameCatalog = gameCatalog;
        _priceDiagnosticLogService = priceDiagnosticLogService;
    }

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "24h";
    [BindProperty(SupportsGet = true)]
    public string Game { get; set; } = "all";
    [BindProperty(SupportsGet = true)]
    public string Source { get; set; } = "all";
    [BindProperty(SupportsGet = true)]
    public string Status { get; set; } = "all";
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? AssetId { get; set; }
    [BindProperty(SupportsGet = true)]
    public string EventType { get; set; } = "all";

    public DateTime FromUtc { get; private set; }
    public List<SummaryCard> SummaryCards { get; private set; } = new();
    public List<PriceDiagnosticEvent> RecentEvents { get; private set; } = new();
    public List<PriceDiagnosticEvent> LastSourceFailures { get; private set; } = new();
    public List<PriceDiagnosticEvent> LastFinalResolutionFailures { get; private set; } = new();
    public List<NoReliablePriceItem> ItemsWithoutReliablePrice { get; private set; } = new();
    public int TotalEventCount { get; private set; }
    public int PageSize { get; private set; } = 100;
    public IReadOnlyList<GameDefinition> SupportedGames => _gameCatalog.SupportedGames;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Range = NormalizeRange(Range);
        Game = NormalizeFilter(Game);
        Source = NormalizeFilter(Source);
        Status = NormalizeFilter(Status);
        EventType = NormalizeFilter(EventType);
        Search = NormalizeNullable(Search);
        AssetId = NormalizeNullable(AssetId);

        FromUtc = DateTime.UtcNow - ResolveRange(Range);
        var gameDefinition = ResolveGame(Game);
        var gameType = gameDefinition is null ? null : (int?)gameDefinition.Type;

        RecentEvents = _priceDiagnosticLogService.GetRecent(
                PageSize,
                Source,
                Status,
                EventType,
                Search,
                gameType,
                FromUtc)
            .Where(item => string.IsNullOrWhiteSpace(AssetId) || string.Equals(item.AssetId, AssetId, StringComparison.Ordinal))
            .ToList();
        TotalEventCount = RecentEvents.Count;
        SummaryCards = BuildSummaryCards(RecentEvents);
        LastSourceFailures = RecentEvents
            .Where(IsSourceFailure)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(PageSize)
            .ToList();
        LastFinalResolutionFailures = RecentEvents
            .Where(IsFinalResolutionFailure)
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(PageSize)
            .ToList();
        ItemsWithoutReliablePrice = BuildItemsWithoutReliablePrice(RecentEvents, this);

        await Task.CompletedTask;
    }

    public string GameName(int? appId, int? gameType = null)
    {
        var match = _gameCatalog.SupportedGames.FirstOrDefault(item =>
            (appId.HasValue && item.SteamAppId == appId.Value) ||
            (gameType.HasValue && (int)item.Type == gameType.Value));
        return match?.DisplayName ?? (appId?.ToString() ?? "Unknown");
    }

    public string ProblemDetails(PriceDiagnosticEvent item)
    {
        var parts = new[]
            {
                item.FailureReason,
                SummarizeDetails(item.DetailsJson)
            }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var text = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(text) ? "-" : Truncate(text, 900);
    }

    private static List<SummaryCard> BuildSummaryCards(IReadOnlyCollection<PriceDiagnosticEvent> logs)
    {
        return
        [
            new("Problem events", logs.Count),
            new("Final failures", logs.Count(IsFinalResolutionFailure)),
            new("Source failures", logs.Count(IsSourceFailure)),
            new("Skinport NotFound", logs.Count(item => item.Source == PriceSourceNames.Skinport && item.EventType == "SourceNotFound")),
            new("DMarket NotFound", logs.Count(item => item.Source == PriceSourceNames.DMarket && item.EventType == "SourceNotFound")),
            new("Steam 429", logs.Count(item => item.Source == PriceSourceNames.Steam && (item.HttpStatusCode == 429 || item.EventType == "SourceRateLimited"))),
            new("CSFloat NotFound", logs.Count(item => item.Source == PriceSourceNames.CSFloat && item.EventType == "SourceNotFound")),
            new("Timeouts", logs.Count(item => item.EventType == "SourceTimeout"))
        ];
    }

    private static bool IsSourceFailure(PriceDiagnosticEvent item)
    {
        return item.EventType is
            "SourceFailed" or
            "SourceNotFound" or
            "SourceReturnedNoUsablePrice" or
            "SourceRateLimited" or
            "SourceSkipped" or
            "SourceTimeout" or
            "SourceRejected" or
            "CurrencyMismatch" or
            "ParseFailed" or
            "ExternalApiError";
    }

    private static bool IsFinalResolutionFailure(PriceDiagnosticEvent item)
    {
        return item.EventType is "FinalResolutionFailed" or "NoReliablePrice";
    }

    private static List<NoReliablePriceItem> BuildItemsWithoutReliablePrice(
        IReadOnlyCollection<PriceDiagnosticEvent> logs,
        PriceDiagnosticsModel model)
    {
        return logs
            .Where(item => item.NormalizedMarketHashName != null &&
                           IsFinalResolutionFailure(item))
            .GroupBy(item => item.NormalizedMarketHashName!)
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.CreatedAtUtc).First();
                var final = group
                    .Where(item => item.EventType is "FinalResolutionFailed" or "NoReliablePrice" ||
                                   string.Equals(item.Status, "NoReliablePrice", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .FirstOrDefault();
                var reasons = ExtractSourceReasons(final?.DetailsJson);
                foreach (var sourceEvent in group
                             .Where(item => !string.IsNullOrWhiteSpace(item.Source))
                             .GroupBy(item => item.Source!, StringComparer.OrdinalIgnoreCase)
                             .Select(sourceGroup => sourceGroup.OrderByDescending(item => item.CreatedAtUtc).First()))
                {
                    if (!reasons.ContainsKey(sourceEvent.Source!))
                    {
                        reasons[sourceEvent.Source!] = BuildProblemReason(sourceEvent);
                    }
                }

                return new NoReliablePriceItem(
                    latest.MarketHashName ?? latest.NormalizedMarketHashName ?? "-",
                    model.GameName(latest.AppId, latest.GameType),
                    final?.FailureReason ?? ExtractString(final?.DetailsJson, "finalReason") ?? latest.FailureReason ?? latest.EventType,
                    reasons.GetValueOrDefault(PriceSourceNames.Skinport) ?? "-",
                    reasons.GetValueOrDefault(PriceSourceNames.DMarket) ?? "-",
                    reasons.GetValueOrDefault(PriceSourceNames.Steam) ?? "-",
                    reasons.GetValueOrDefault(PriceSourceNames.CSFloat) ?? "-",
                    latest.CreatedAtUtc);
            })
            .OrderByDescending(item => item.LastAttemptUtc)
            .Take(50)
            .ToList();
    }

    private static Dictionary<string, string> ExtractSourceReasons(string? detailsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (!document.RootElement.TryGetProperty("sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var source in sources.EnumerateArray())
            {
                var sourceName = source.TryGetProperty("source", out var sourceNameElement)
                    ? sourceNameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    continue;
                }

                var state = source.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
                var reason = source.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                var status = source.TryGetProperty("Status", out var statusElement) ? statusElement.GetString() : null;
                result[sourceName] = string.Join(": ", new[] { state ?? status, reason }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string BuildProblemReason(PriceDiagnosticEvent item)
    {
        return Truncate(string.Join(" | ", new[]
        {
            item.FailureReason ?? item.EventType,
            SummarizeDetails(item.DetailsJson)
        }.Where(value => !string.IsNullOrWhiteSpace(value))), 500);
    }

    private static string? SummarizeDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("misses", out var misses) &&
                misses.ValueKind == JsonValueKind.Array)
            {
                var parts = misses
                    .EnumerateArray()
                    .Select(miss =>
                    {
                        var endpoint = miss.TryGetProperty("endpoint", out var endpointElement)
                            ? endpointElement.GetString()
                            : "endpoint";
                        var returnedCount = miss.TryGetProperty("returnedCount", out var countElement) && countElement.TryGetInt32(out var count)
                            ? count
                            : (int?)null;
                        return returnedCount.HasValue ? $"{endpoint}: returnedCount={returnedCount}" : endpoint;
                    })
                    .ToList();
                return parts.Count == 0 ? null : string.Join("; ", parts);
            }

            if (root.TryGetProperty("sources", out var sources) &&
                sources.ValueKind == JsonValueKind.Array)
            {
                var parts = sources
                    .EnumerateArray()
                    .Select(source =>
                    {
                        var sourceName = source.TryGetProperty("source", out var sourceElement)
                            ? sourceElement.GetString()
                            : "source";
                        var state = source.TryGetProperty("state", out var stateElement)
                            ? stateElement.GetString()
                            : null;
                        var reason = source.TryGetProperty("reason", out var reasonElement)
                            ? reasonElement.GetString()
                            : null;
                        return $"{sourceName}: {string.Join(" - ", new[] { state, reason }.Where(value => !string.IsNullOrWhiteSpace(value)))}";
                    })
                    .ToList();
                return parts.Count == 0 ? null : string.Join("; ", parts);
            }

            var fields = new List<string>();
            foreach (var name in new[] { "endpoint", "requestedNormalizedName", "returnedItemsCount", "returnedCount", "requestedTitle", "gameId", "httpStatus", "usedField", "exceptionType" })
            {
                if (root.TryGetProperty(name, out var property))
                {
                    fields.Add($"{name}={FormatJsonValue(property)}");
                }
            }

            return fields.Count == 0 ? Truncate(detailsJson, 400) : string.Join("; ", fields);
        }
        catch (JsonException)
        {
            return Truncate(detailsJson, 400);
        }
    }

    private static string FormatJsonValue(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string? ExtractString(string? detailsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.TryGetProperty(propertyName, out var property)
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private GameDefinition? ResolveGame(string game)
    {
        if (string.Equals(game, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _gameCatalog.SupportedGames
            .FirstOrDefault(item => string.Equals(item.Key, game, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(item.Type.ToString(), game, StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan ResolveRange(string range)
    {
        return range switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromHours(24)
        };
    }

    private static string NormalizeRange(string? value)
    {
        return value is "15m" or "1h" or "6h" or "24h" or "7d" ? value : "24h";
    }

    private static string NormalizeFilter(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "all" : value.Trim();
    }

    private static string? NormalizeNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed record SummaryCard(string Label, int Value);
    public sealed record ProblemItem(string MarketHashName, string Reason, int Count, string? FailureReason);
    public sealed record NoReliablePriceItem(
        string MarketHashName,
        string Game,
        string FinalReason,
        string SkinportReason,
        string DMarketReason,
        string SteamReason,
        string CSFloatReason,
        DateTime LastAttemptUtc);

    public sealed class PriceIssueItem
    {
        public int AppId { get; set; }
        public string MarketHashName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string PriceType { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string? FailureReason { get; set; }
        public decimal ConfidenceScore { get; set; }
    }
}
