using Microsoft.EntityFrameworkCore;
using SkinMarket.Models;

namespace SkinMarket.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<BalanceTransaction> BalanceTransactions => Set<BalanceTransaction>();
    public DbSet<MarketItem> MarketItems => Set<MarketItem>();
    public DbSet<TradeOperation> TradeOperations => Set<TradeOperation>();

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
            entity.Property(user => user.Balance).HasDefaultValue(0m);
            entity.HasIndex(user => user.SteamId).IsUnique();
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
            entity.Property(operation => operation.ItemName).IsRequired();
            entity.Property(operation => operation.Status).IsRequired();
            entity.Property(operation => operation.TradeOfferId).HasMaxLength(100);
            entity.Property(operation => operation.BotTradeUrl).HasMaxLength(500);
            entity.Property(operation => operation.CreditAmount).HasDefaultValue(0m);
            entity.HasIndex(operation => new { operation.AppUserId, operation.AssetId, operation.Status });
            entity.HasOne(operation => operation.AppUser)
                .WithMany(user => user.TradeOperations)
                .HasForeignKey(operation => operation.AppUserId);
        });

        modelBuilder.Entity<MarketItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.AssetId).IsRequired();
            entity.Property(item => item.ClassId).IsRequired();
            entity.Property(item => item.InstanceId).IsRequired();
            entity.Property(item => item.ItemName).IsRequired();
            entity.Property(item => item.Status).IsRequired();
            entity.Property(item => item.DeliveryStatus).HasMaxLength(100);
            entity.Property(item => item.DeliveryTradeOfferId).HasMaxLength(100);
            entity.HasIndex(item => item.SourceTradeOperationId).IsUnique();
            entity.HasOne(item => item.SourceTradeOperation)
                .WithMany()
                .HasForeignKey(item => item.SourceTradeOperationId);
            entity.HasOne(item => item.BuyerAppUser)
                .WithMany()
                .HasForeignKey(item => item.BuyerAppUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
