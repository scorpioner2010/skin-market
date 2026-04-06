using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryFieldsToMarketItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAtUtc",
                table: "MarketItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryErrorMessage",
                table: "MarketItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryTradeOfferId",
                table: "MarketItems",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveredAtUtc",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "DeliveryErrorMessage",
                table: "MarketItems");

            migrationBuilder.DropColumn(
                name: "DeliveryTradeOfferId",
                table: "MarketItems");
        }
    }
}
