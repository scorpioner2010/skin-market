using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddItemChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ItemChatThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemNameSnapshot = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ItemImageUrlSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ItemPriceSnapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LastMessagePreview = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastMessageAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemChatThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemChatThreads_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemChatThreads_ServiceItems_ServiceItemId",
                        column: x => x.ServiceItemId,
                        principalTable: "ServiceItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ItemChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemChatThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorAppUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemChatMessages_AppUsers_AuthorAppUserId",
                        column: x => x.AuthorAppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ItemChatMessages_ItemChatThreads_ItemChatThreadId",
                        column: x => x.ItemChatThreadId,
                        principalTable: "ItemChatThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatMessages_AuthorAppUserId",
                table: "ItemChatMessages",
                column: "AuthorAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatMessages_ItemChatThreadId_CreatedAtUtc",
                table: "ItemChatMessages",
                columns: new[] { "ItemChatThreadId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_AppUserId_ServiceItemId",
                table: "ItemChatThreads",
                columns: new[] { "AppUserId", "ServiceItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_LastMessageAtUtc",
                table: "ItemChatThreads",
                column: "LastMessageAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_ServiceItemId",
                table: "ItemChatThreads",
                column: "ServiceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_UpdatedAtUtc",
                table: "ItemChatThreads",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemChatMessages");

            migrationBuilder.DropTable(
                name: "ItemChatThreads");
        }
    }
}
