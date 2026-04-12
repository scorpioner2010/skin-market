using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamTradeBotTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppId",
                table: "TradeOperations",
                type: "integer",
                nullable: false,
                defaultValue: 730);

            migrationBuilder.AddColumn<string>(
                name: "BotAssetId",
                table: "TradeOperations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotClassId",
                table: "TradeOperations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotInstanceId",
                table: "TradeOperations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextId",
                table: "TradeOperations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "2");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReceivedByBotAtUtc",
                table: "TradeOperations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "BotAssetId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "BotClassId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "BotInstanceId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "ContextId",
                table: "TradeOperations");

            migrationBuilder.DropColumn(
                name: "ReceivedByBotAtUtc",
                table: "TradeOperations");
        }
    }
}
