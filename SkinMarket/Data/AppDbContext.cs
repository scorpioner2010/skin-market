using Microsoft.EntityFrameworkCore;
using SkinMarket.Models;

namespace SkinMarket.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AppLog> Logs => Set<AppLog>();
    public DbSet<BalanceTransaction> BalanceTransactions => Set<BalanceTransaction>();
    public DbSet<MarketPurchaseRecord> MarketPurchaseRecords => Set<MarketPurchaseRecord>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<TradeOperation> TradeOperations => Set<TradeOperation>();
    public DbSet<ServiceItem> ServiceItems => Set<ServiceItem>();
    public DbSet<ItemChatThread> ItemChatThreads => Set<ItemChatThread>();
    public DbSet<ItemChatMessage> ItemChatMessages => Set<ItemChatMessage>();
    public DbSet<MinefieldGameSession> MinefieldGameSessions => Set<MinefieldGameSession>();
    public DbSet<MinefieldGameSettings> MinefieldGameSettings => Set<MinefieldGameSettings>();
    public DbSet<NavigationMenuSetting> NavigationMenuSettings => Set<NavigationMenuSetting>();
    public DbSet<SteamInventoryCacheEntry> SteamInventoryCacheEntries => Set<SteamInventoryCacheEntry>();
    public DbSet<SteamInventorySnapshot> SteamInventorySnapshots => Set<SteamInventorySnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.Property(user => user.SteamId).IsRequired();
            entity.Property(user => user.DisplayName).IsRequired();
            entity.Property(user => user.PersonaName).HasMaxLength(200);
            entity.Property(user => user.AvatarUrl).HasMaxLength(500);
            entity.Property(user => user.TradeUrl).HasMaxLength(500);
            entity.Property(user => user.IsAdmin).HasDefaultValue(false);
            entity.Property(user => user.Balance).HasDefaultValue(0m);
            entity.HasIndex(user => user.SteamId).IsUnique();
        });

        modelBuilder.Entity<AppLog>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Level).IsRequired().HasMaxLength(20);
            entity.Property(item => item.Message).IsRequired().HasMaxLength(4000);
            entity.Property(item => item.Source).HasMaxLength(200);
            entity.Property(item => item.StackTrace).HasMaxLength(12000);
            entity.HasIndex(item => item.TimestampUtc);
        });

        modelBuilder.Entity<BalanceTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.Property(transaction => transaction.Type).IsRequired();
            entity.HasOne(transaction => transaction.AppUser)
                .WithMany(user => user.BalanceTransactions)
                .HasForeignKey(transaction => transaction.AppUserId);
        });

        modelBuilder.Entity<TradeOperation>(entity =>
        {
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.SteamId).IsRequired();
            entity.Property(operation => operation.AssetId).IsRequired();
            entity.Property(operation => operation.ClassId).IsRequired();
            entity.Property(operation => operation.InstanceId).IsRequired();
            entity.Property(operation => operation.AppId).HasDefaultValue(730);
            entity.Property(operation => operation.ContextId).IsRequired().HasMaxLength(20).HasDefaultValue("2");
            entity.Property(operation => operation.ItemName).IsRequired();
            entity.Property(operation => operation.MarketHashName).HasMaxLength(300);
            entity.Property(operation => operation.Status).IsRequired();
            entity.Property(operation => operation.TradeOfferId).HasMaxLength(100);
            entity.Property(operation => operation.BotTradeUrl).HasMaxLength(500);
            entity.Property(operation => operation.BotAssetId).HasMaxLength(100);
            entity.Property(operation => operation.BotClassId).HasMaxLength(100);
            entity.Property(operation => operation.BotInstanceId).HasMaxLength(100);
            entity.Property(operation => operation.CreditAmount).HasDefaultValue(0m);
            entity.HasIndex(operation => new { operation.AppUserId, operation.AssetId, operation.Status });
            entity.HasOne(operation => operation.AppUser)
                .WithMany(user => user.TradeOperations)
                .HasForeignKey(operation => operation.AppUserId);
        });

        modelBuilder.Entity<MarketPurchaseRecord>(entity =>
        {
            entity.ToTable("MarketPurchaseRecords");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.AppId).HasDefaultValue(730);
            entity.Property(item => item.ContextId).IsRequired().HasMaxLength(20).HasDefaultValue("2");
            entity.Property(item => item.AssetId).IsRequired();
            entity.Property(item => item.ClassId).IsRequired();
            entity.Property(item => item.InstanceId).IsRequired();
            entity.Property(item => item.ItemName).IsRequired();
            entity.Property(item => item.MarketHashName).HasMaxLength(300);
            entity.Property(item => item.Status).IsRequired();
            entity.Property(item => item.DeliveryStatus).HasMaxLength(100);
            entity.Property(item => item.DeliveryTradeOfferId).HasMaxLength(100);
            entity.HasIndex(item => new { item.AppId, item.ContextId, item.AssetId }).IsUnique();
            entity.HasIndex(item => item.SourceTradeOperationId).IsUnique();
            entity.HasOne(item => item.SourceTradeOperation)
                .WithMany()
                .HasForeignKey(item => item.SourceTradeOperationId);
            entity.HasOne(item => item.BuyerAppUser)
                .WithMany()
                .HasForeignKey(item => item.BuyerAppUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceSnapshot>(entity =>
        {
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.MarketHashName).IsRequired().HasMaxLength(300);
            entity.Property(snapshot => snapshot.VariantKey).HasMaxLength(100);
            entity.Property(snapshot => snapshot.Currency).IsRequired().HasMaxLength(10);
            entity.Property(snapshot => snapshot.Source).IsRequired().HasMaxLength(50);
            entity.Property(snapshot => snapshot.SourceItemId).HasMaxLength(100);
            entity.Property(snapshot => snapshot.PriceType).IsRequired().HasMaxLength(50);
            entity.Property(snapshot => snapshot.Price).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.PriceUsd).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.OriginalPrice).HasPrecision(18, 4);
            entity.Property(snapshot => snapshot.FxRate).HasPrecision(18, 8);
            entity.Property(snapshot => snapshot.BestBidUsd).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.BestAskUsd).HasPrecision(18, 2);
            entity.Property(snapshot => snapshot.ConfidenceScore).HasPrecision(5, 4);
            entity.Property(snapshot => snapshot.Status).IsRequired().HasMaxLength(50);
            entity.Property(snapshot => snapshot.FailureReason).HasMaxLength(500);
            entity.Property(snapshot => snapshot.RawPayloadHash).HasMaxLength(128);
            entity.Property(snapshot => snapshot.ProvenanceJson).HasMaxLength(4000);
            entity.HasIndex(snapshot => new { snapshot.AppId, snapshot.MarketHashName, snapshot.Currency, snapshot.Source, snapshot.PriceType }).IsUnique();
            entity.HasIndex(snapshot => snapshot.ExpiresAtUtc);
        });

        modelBuilder.Entity<ServiceItem>(entity =>
        {
            entity.ToTable("ServiceItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired().HasMaxLength(160);
            entity.Property(item => item.Description).HasMaxLength(1000);
            entity.Property(item => item.Price).HasPrecision(18, 2);
            entity.Property(item => item.ImageUrl).IsRequired().HasMaxLength(500);
            entity.Property(item => item.ImageStoragePath).IsRequired().HasMaxLength(500);
            entity.Property(item => item.ImageFileName).HasMaxLength(260);
            entity.Property(item => item.ImageContentType).HasMaxLength(100);
            entity.HasIndex(item => item.Name);
            entity.HasIndex(item => item.CreatedAtUtc);
        });

        modelBuilder.Entity<ItemChatThread>(entity =>
        {
            entity.ToTable("ItemChatThreads");
            entity.HasKey(thread => thread.Id);
            entity.Property(thread => thread.ItemNameSnapshot).IsRequired().HasMaxLength(160);
            entity.Property(thread => thread.ItemImageUrlSnapshot).HasMaxLength(500);
            entity.Property(thread => thread.ItemPriceSnapshot).HasPrecision(18, 2);
            entity.Property(thread => thread.LastMessagePreview).HasMaxLength(300);
            entity.HasIndex(thread => new { thread.AppUserId, thread.ServiceItemId }).IsUnique();
            entity.HasIndex(thread => thread.LastMessageAtUtc);
            entity.HasIndex(thread => thread.LastUserMessageAtUtc);
            entity.HasIndex(thread => new { thread.AppUserId, thread.LastAdminMessageAtUtc });
            entity.HasIndex(thread => thread.UpdatedAtUtc);
            entity.HasOne(thread => thread.AppUser)
                .WithMany(user => user.ItemChatThreads)
                .HasForeignKey(thread => thread.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(thread => thread.ServiceItem)
                .WithMany(item => item.ChatThreads)
                .HasForeignKey(thread => thread.ServiceItemId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ItemChatMessage>(entity =>
        {
            entity.ToTable("ItemChatMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.AuthorType).IsRequired().HasMaxLength(20);
            entity.Property(message => message.Body).IsRequired().HasMaxLength(4000);
            entity.HasIndex(message => new { message.ItemChatThreadId, message.CreatedAtUtc });
            entity.HasOne(message => message.ItemChatThread)
                .WithMany(thread => thread.Messages)
                .HasForeignKey(message => message.ItemChatThreadId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(message => message.AuthorAppUser)
                .WithMany(user => user.ItemChatMessages)
                .HasForeignKey(message => message.AuthorAppUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MinefieldGameSession>(entity =>
        {
            entity.ToTable("MinefieldGameSessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.BetAmount).HasPrecision(18, 2);
            entity.Property(session => session.Status).IsRequired().HasMaxLength(32);
            entity.Property(session => session.ResultSteps).IsRequired().HasMaxLength(64);
            entity.Property(session => session.MultipliersJson).IsRequired().HasMaxLength(1000);
            entity.Property(session => session.PayoutAmount).HasPrecision(18, 2);
            entity.HasIndex(session => new { session.AppUserId, session.Status });
            entity.HasIndex(session => session.CreatedAtUtc);
            entity.HasOne(session => session.AppUser)
                .WithMany()
                .HasForeignKey(session => session.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MinefieldGameSettings>(entity =>
        {
            entity.ToTable("MinefieldGameSettings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.GameKey).IsRequired().HasMaxLength(64);
            entity.Property(settings => settings.IsEnabled).HasDefaultValue(true);
            entity.Property(settings => settings.MinimumBet).HasPrecision(18, 2);
            entity.Property(settings => settings.MaximumBet).HasPrecision(18, 2);
            entity.Property(settings => settings.ReturnToPlayer).HasPrecision(6, 4);
            entity.Property(settings => settings.StepSafeChancesJson).HasMaxLength(1000);
            entity.Property(settings => settings.StepMultipliersJson).HasMaxLength(1000);
            entity.HasIndex(settings => settings.GameKey).IsUnique();
        });

        modelBuilder.Entity<NavigationMenuSetting>(entity =>
        {
            entity.ToTable("NavigationMenuSettings");
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.Key).IsRequired().HasMaxLength(64);
            entity.Property(settings => settings.DisplayName).IsRequired().HasMaxLength(100);
            entity.Property(settings => settings.IsEnabled).HasDefaultValue(true);
            entity.HasIndex(settings => settings.Key).IsUnique();
        });

        modelBuilder.Entity<SteamInventoryCacheEntry>(entity =>
        {
            entity.ToTable("SteamInventoryCacheEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.SteamId).IsRequired().HasMaxLength(32);
            entity.Property(entry => entry.ContextId).IsRequired().HasMaxLength(20);
            entity.Property(entry => entry.ItemsJson).IsRequired();
            entity.HasIndex(entry => new { entry.SteamId, entry.AppId, entry.ContextId }).IsUnique();
            entity.HasIndex(entry => entry.ExpiresAtUtc);
        });

        modelBuilder.Entity<SteamInventorySnapshot>(entity =>
        {
            entity.ToTable("SteamInventorySnapshots");
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.SteamId).IsRequired().HasMaxLength(32);
            entity.Property(snapshot => snapshot.ItemsJson).IsRequired();
            entity.Property(snapshot => snapshot.LastErrorCode).HasMaxLength(64);
            entity.Property(snapshot => snapshot.LastErrorMessage).HasMaxLength(1000);
            entity.HasIndex(snapshot => new { snapshot.SteamId, snapshot.GameType }).IsUnique();
            entity.HasIndex(snapshot => snapshot.NextAllowedRefreshUtc);
            entity.HasIndex(snapshot => snapshot.RefreshInProgress);
        });
    }
}
