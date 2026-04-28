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
            entity.Property(snapshot => snapshot.Currency).IsRequired().HasMaxLength(10);
            entity.Property(snapshot => snapshot.Source).IsRequired().HasMaxLength(50);
            entity.Property(snapshot => snapshot.Status).IsRequired().HasMaxLength(50);
            entity.Property(snapshot => snapshot.FailureReason).HasMaxLength(500);
            entity.HasIndex(snapshot => new { snapshot.AppId, snapshot.MarketHashName, snapshot.Currency }).IsUnique();
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
    }
}
