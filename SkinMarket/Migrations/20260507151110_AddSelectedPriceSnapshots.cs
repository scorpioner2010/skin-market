using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedPriceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots");

            migrationBuilder.AddColumn<bool>(
                name: "IsSelected",
                table: "PriceSnapshots",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_IsSelected",
                table: "PriceSnapshots",
                columns: new[] { "AppId", "MarketHashName", "Currency", "IsSelected" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots",
                columns: new[] { "AppId", "MarketHashName", "Currency", "Source", "PriceType", "IsSelected" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_IsSelected",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "IsSelected",
                table: "PriceSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots",
                columns: new[] { "AppId", "MarketHashName", "Currency", "Source", "PriceType" },
                unique: true);
        }
    }
}
