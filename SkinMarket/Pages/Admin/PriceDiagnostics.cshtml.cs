using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;

namespace SkinMarket.Pages.Admin;

public class PriceDiagnosticsModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public PriceDiagnosticsModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public List<PriceSourceSummary> SourceSummaries { get; private set; } = new();
    public List<PriceIssueItem> MissingPriceItems { get; private set; } = new();
    public List<PriceIssueItem> StalePriceItems { get; private set; } = new();
    public IReadOnlyDictionary<string, int> SelectedSourceDistribution { get; private set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ConfidenceDistribution { get; private set; } = new Dictionary<string, int>();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var snapshots = await _dbContext.PriceSnapshots
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(5000)
            .ToListAsync(cancellationToken);

        SourceSummaries = snapshots
            .GroupBy(item => item.Source)
            .Select(group => new PriceSourceSummary
            {
                Source = group.Key,
                SnapshotCount = group.Count(),
                LastSuccessUtc = group
                    .Where(item => item.HasPrice)
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .Select(item => (DateTime?)item.UpdatedAtUtc)
                    .FirstOrDefault(),
                LastFailureUtc = group
                    .Where(item => !item.HasPrice)
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .Select(item => (DateTime?)item.UpdatedAtUtc)
                    .FirstOrDefault(),
                LastFailureReason = group
                    .Where(item => !item.HasPrice && item.FailureReason != null)
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .Select(item => item.FailureReason)
                    .FirstOrDefault(),
                ErrorCount = group.Count(item => !item.HasPrice),
                RateLimitCount = group.Count(item => item.FailureReason != null &&
                                                     (item.FailureReason.Contains("429", StringComparison.OrdinalIgnoreCase) ||
                                                      item.FailureReason.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            })
            .OrderBy(item => item.Source)
            .ToList();

        MissingPriceItems = snapshots
            .Where(item => !item.HasPrice)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Take(25)
            .Select(ToIssueItem)
            .ToList();

        StalePriceItems = snapshots
            .Where(item => item.HasPrice && item.ExpiresAtUtc <= now)
            .OrderBy(item => item.ExpiresAtUtc)
            .Take(25)
            .Select(ToIssueItem)
            .ToList();

        SelectedSourceDistribution = snapshots
            .Where(item => item.HasPrice)
            .GroupBy(item => $"{item.Source}/{item.PriceType}")
            .OrderByDescending(group => group.Count())
            .Take(12)
            .ToDictionary(group => group.Key, group => group.Count());

        ConfidenceDistribution = snapshots
            .Where(item => item.HasPrice)
            .GroupBy(item => item.ConfidenceScore >= 0.8m ? "High" : item.ConfidenceScore >= 0.55m ? "Medium" : "Low")
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private static PriceIssueItem ToIssueItem(Models.PriceSnapshot snapshot)
    {
        return new PriceIssueItem
        {
            AppId = snapshot.AppId,
            MarketHashName = snapshot.MarketHashName,
            Source = snapshot.Source,
            PriceType = snapshot.PriceType,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
            ExpiresAtUtc = snapshot.ExpiresAtUtc,
            FailureReason = snapshot.FailureReason,
            ConfidenceScore = snapshot.ConfidenceScore
        };
    }

    public class PriceSourceSummary
    {
        public string Source { get; set; } = string.Empty;
        public int SnapshotCount { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastFailureReason { get; set; }
        public int ErrorCount { get; set; }
        public int RateLimitCount { get; set; }
    }

    public class PriceIssueItem
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
