using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AllowRepeatedMarketPurchaseRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords",
                columns: new[] { "AppId", "ContextId", "AssetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords",
                columns: new[] { "AppId", "ContextId", "AssetId" },
                unique: true);
        }
    }
}
