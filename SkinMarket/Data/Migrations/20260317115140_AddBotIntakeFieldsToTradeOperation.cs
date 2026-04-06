using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBotIntakeFieldsToTradeOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BotTradeUrl",
                table: "TradeOperations",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "TradeOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradeOfferId",
                table: "TradeOperations",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "TradeOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BotTradeUrl",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "TradeOfferId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "TradeOperations");
        }
    }
}
