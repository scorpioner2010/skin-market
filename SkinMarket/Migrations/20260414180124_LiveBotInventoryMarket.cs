using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class LiveBotInventoryMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MarketItems_AppUsers_BuyerAppUserId",
                table: "MarketItems");

            migrationBuilder.DropForeignKey(
                name: "FK_MarketItems_TradeOperations_SourceTradeOperationId",
                table: "MarketItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MarketItems",
                table: "MarketItems");

            migrationBuilder.RenameTable(
                name: "MarketItems",
                newName: "MarketPurchaseRecords");

            migrationBuilder.RenameIndex(
                name: "IX_MarketItems_SourceTradeOperationId",
                table: "MarketPurchaseRecords",
                newName: "IX_MarketPurchaseRecords_SourceTradeOperationId");

            migrationBuilder.RenameIndex(
                name: "IX_MarketItems_BuyerAppUserId",
                table: "MarketPurchaseRecords",
                newName: "IX_MarketPurchaseRecords_BuyerAppUserId");

            migrationBuilder.AddColumn<int>(
                name: "AppId",
                table: "MarketPurchaseRecords",
                type: "integer",
                nullable: false,
                defaultValue: 730);

            migrationBuilder.AddColumn<string>(
                name: "ContextId",
                table: "MarketPurchaseRecords",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "2");

            migrationBuilder.AddColumn<int>(
                name: "GameType",
                table: "MarketPurchaseRecords",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceTradeOperationId",
                table: "MarketPurchaseRecords",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MarketPurchaseRecords",
                table: "MarketPurchaseRecords",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_MarketPurchaseRecords_AppId_ContextId_AssetId",
                table: "MarketPurchaseRecords",
                columns: new[] { "AppId", "ContextId", "AssetId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MarketPurchaseRecords_AppUsers_BuyerAppUserId",
                table: "MarketPurchaseRecords",
                column: "BuyerAppUserId",
                principalTable: "AppUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MarketPurchaseRecords_TradeOperations_SourceTradeOperationId",
                table: "MarketPurchaseRecords",
                column: "SourceTradeOperationId",
                principalTable: "TradeOperations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketPurchaseRecords");

            migrationBuilder.CreateTable(
                name: "MarketItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceTradeOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<string>(type: "text", nullable: false),
                    ClassId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveryErrorMessage = table.Column<string>(type: "text", nullable: true),
                    DeliveryStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DeliveryTradeOfferId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IconUrl = table.Column<string>(type: "text", nullable: true),
                    InstanceId = table.Column<string>(type: "text", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    MarketHashName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    PurchasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketItems_AppUsers_BuyerAppUserId",
                        column: x => x.BuyerAppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketItems_TradeOperations_SourceTradeOperationId",
                        column: x => x.SourceTradeOperationId,
                        principalTable: "TradeOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketItems_BuyerAppUserId",
                table: "MarketItems",
                column: "BuyerAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketItems_SourceTradeOperationId",
                table: "MarketItems",
                column: "SourceTradeOperationId",
                unique: true);
        }
    }
}
