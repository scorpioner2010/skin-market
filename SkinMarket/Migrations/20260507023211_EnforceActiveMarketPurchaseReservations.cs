using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class EnforceActiveMarketPurchaseReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords");

            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_SourceTradeOperationId",
                table: "MarketPurchaseRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords",
                columns: new[] { "AppId", "ContextId", "AssetId" },
                unique: true,
                filter: "\"Status\" = 'Sold' AND (\"DeliveryStatus\" IS NULL OR \"DeliveryStatus\" IN ('PendingDelivery', 'DeliveryBotPending', 'AwaitingBotConfirmation', 'DeliveryTradeCreated', 'AwaitingBuyerAction', 'DeliveryInEscrow', 'Delivered'))");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_SourceTradeOperationId",
                table: "MarketPurchaseRecords",
                column: "SourceTradeOperationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords");

            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_SourceTradeOperationId",
                table: "MarketPurchaseRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords",
                columns: new[] { "AppId", "ContextId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_SourceTradeOperationId",
                table: "MarketPurchaseRecords",
                column: "SourceTradeOperationId",
                unique: true);
        }
    }
}
