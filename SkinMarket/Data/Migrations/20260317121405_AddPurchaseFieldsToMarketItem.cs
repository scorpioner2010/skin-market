using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseFieldsToMarketItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BuyerAppUserId",
                table: "MarketItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryStatus",
                table: "MarketItems",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PurchasedAtUtc",
                table: "MarketItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "MarketItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_MarketItems_BuyerAppUserId",
                table: "MarketItems",
                column: "BuyerAppUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_MarketItems_AppUsers_BuyerAppUserId",
                table: "MarketItems",
                column: "BuyerAppUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MarketItems_AppUsers_BuyerAppUserId",
                table: "MarketItems");

            migrationBuilder.DropIndex(
                name: "IX_MarketItems_BuyerAppUserId",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "BuyerAppUserId",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "PurchasedAtUtc",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "MarketItems");
        }
    }
}
