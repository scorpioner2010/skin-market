using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SkinMarket.Data;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260501001002_AddNavigationMenuSettings")]
    public partial class AddNavigationMenuSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NavigationMenuSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavigationMenuSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NavigationMenuSettings_Key",
                table: "NavigationMenuSettings",
                column: "Key",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "NavigationMenuSettings"
                    ("Id", "Key", "DisplayName", "IsEnabled", "SortOrder", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    ('0b7e88a4-d275-4890-bb9b-b00d06981f1f', 'items', 'Items', TRUE, 10, NOW(), NOW()),
                    ('8de2b645-c98d-4659-a3ec-a8f0fd80bb85', 'games', 'Games', TRUE, 20, NOW(), NOW()),
                    ('9b0f9b73-7c4e-4985-87e0-93a4692a15f1', 'chats', 'Chats', TRUE, 30, NOW(), NOW()),
                    ('d55d765b-90d0-4f68-9552-7bfaa3a26de4', 'market', 'Market', TRUE, 40, NOW(), NOW()),
                    ('54a0cb68-5d18-4a03-9643-52fe664375a3', 'inventory', 'Inventory', TRUE, 50, NOW(), NOW()),
                    ('70dd7ccd-5ff3-47b0-b855-82ec154efb4c', 'history', 'History', TRUE, 60, NOW(), NOW());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NavigationMenuSettings");
        }
    }
}
