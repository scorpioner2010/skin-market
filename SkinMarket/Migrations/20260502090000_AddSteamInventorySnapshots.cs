using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SkinMarket.Data;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260502090000_AddSteamInventorySnapshots")]
    public partial class AddSteamInventorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SteamInventorySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SteamId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GameType = table.Column<int>(type: "integer", nullable: false),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    LastSuccessRefreshUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RateLimitStrikeCount = table.Column<int>(type: "integer", nullable: false),
                    NextAllowedRefreshUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshInProgress = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamInventorySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SteamInventorySnapshots_NextAllowedRefreshUtc",
                table: "SteamInventorySnapshots",
                column: "NextAllowedRefreshUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SteamInventorySnapshots_RefreshInProgress",
                table: "SteamInventorySnapshots",
                column: "RefreshInProgress");

            migrationBuilder.CreateIndex(
                name: "IX_SteamInventorySnapshots_SteamId_GameType",
                table: "SteamInventorySnapshots",
                columns: new[] { "SteamId", "GameType" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "SteamInventorySnapshots"
                    ("Id", "SteamId", "GameType", "ItemsJson", "LastSuccessRefreshUtc", "LastAttemptUtc",
                     "LastErrorCode", "LastErrorMessage", "RateLimitStrikeCount", "NextAllowedRefreshUtc", "RefreshInProgress")
                SELECT
                    md5("SteamId" || ':' || "GameType"::text)::uuid,
                    "SteamId",
                    "GameType",
                    "ItemsJson",
                    "FetchedAtUtc",
                    "FetchedAtUtc",
                    NULL,
                    NULL,
                    0,
                    NULL,
                    FALSE
                FROM "SteamInventoryCacheEntries"
                ON CONFLICT ("SteamId", "GameType") DO UPDATE SET
                    "ItemsJson" = EXCLUDED."ItemsJson",
                    "LastSuccessRefreshUtc" = EXCLUDED."LastSuccessRefreshUtc",
                    "LastAttemptUtc" = EXCLUDED."LastAttemptUtc";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SteamInventorySnapshots");
        }
    }
}
