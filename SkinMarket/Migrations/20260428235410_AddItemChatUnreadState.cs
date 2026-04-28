using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddItemChatUnreadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AdminLastReadAtUtc",
                table: "ItemChatThreads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAdminMessageAtUtc",
                table: "ItemChatThreads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUserMessageAtUtc",
                table: "ItemChatThreads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UserLastReadAtUtc",
                table: "ItemChatThreads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ItemChatThreads" AS thread
                SET "LastUserMessageAtUtc" = latest_message."CreatedAtUtc"
                FROM (
                    SELECT "ItemChatThreadId", MAX("CreatedAtUtc") AS "CreatedAtUtc"
                    FROM "ItemChatMessages"
                    WHERE "AuthorType" = 'User'
                    GROUP BY "ItemChatThreadId"
                ) AS latest_message
                WHERE thread."Id" = latest_message."ItemChatThreadId";
                """);

            migrationBuilder.Sql("""
                UPDATE "ItemChatThreads" AS thread
                SET "LastAdminMessageAtUtc" = latest_message."CreatedAtUtc"
                FROM (
                    SELECT "ItemChatThreadId", MAX("CreatedAtUtc") AS "CreatedAtUtc"
                    FROM "ItemChatMessages"
                    WHERE "AuthorType" = 'Admin'
                    GROUP BY "ItemChatThreadId"
                ) AS latest_message
                WHERE thread."Id" = latest_message."ItemChatThreadId";
                """);

            migrationBuilder.Sql("""
                UPDATE "ItemChatThreads"
                SET "AdminLastReadAtUtc" = "LastUserMessageAtUtc"
                WHERE "LastUserMessageAtUtc" IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "ItemChatThreads"
                SET "UserLastReadAtUtc" = "LastAdminMessageAtUtc"
                WHERE "LastAdminMessageAtUtc" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_AppUserId_LastAdminMessageAtUtc",
                table: "ItemChatThreads",
                columns: new[] { "AppUserId", "LastAdminMessageAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemChatThreads_LastUserMessageAtUtc",
                table: "ItemChatThreads",
                column: "LastUserMessageAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ItemChatThreads_AppUserId_LastAdminMessageAtUtc",
                table: "ItemChatThreads");

            migrationBuilder.DropIndex(
                name: "IX_ItemChatThreads_LastUserMessageAtUtc",
                table: "ItemChatThreads");

            migrationBuilder.DropColumn(
                name: "AdminLastReadAtUtc",
                table: "ItemChatThreads");

            migrationBuilder.DropColumn(
                name: "LastAdminMessageAtUtc",
                table: "ItemChatThreads");

            migrationBuilder.DropColumn(
                name: "LastUserMessageAtUtc",
                table: "ItemChatThreads");

            migrationBuilder.DropColumn(
                name: "UserLastReadAtUtc",
                table: "ItemChatThreads");
        }
    }
}
