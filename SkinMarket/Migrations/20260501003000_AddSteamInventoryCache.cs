using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamInventoryCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SteamInventoryCacheEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SteamId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GameType = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<int>(type: "integer", nullable: false),
                    ContextId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamInventoryCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SteamInventoryCacheEntries_ExpiresAtUtc",
                table: "SteamInventoryCacheEntries",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SteamInventoryCacheEntries_SteamId_AppId_ContextId",
                table: "SteamInventoryCacheEntries",
                columns: new[] { "SteamId", "AppId", "ContextId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SteamInventoryCacheEntries");
        }
    }
}
