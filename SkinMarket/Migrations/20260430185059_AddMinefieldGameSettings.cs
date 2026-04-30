using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SkinMarket.Data;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260430185059_AddMinefieldGameSettings")]
    public partial class AddMinefieldGameSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MinefieldGameSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    MinimumBet = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaximumBet = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Rows = table.Column<int>(type: "integer", nullable: false),
                    Columns = table.Column<int>(type: "integer", nullable: false),
                    MinesPerLine = table.Column<int>(type: "integer", nullable: false),
                    ReturnToPlayer = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    UseCustomStepSafeChances = table.Column<bool>(type: "boolean", nullable: false),
                    StepSafeChancesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MinefieldGameSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MinefieldGameSettings_GameKey",
                table: "MinefieldGameSettings",
                column: "GameKey",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "MinefieldGameSettings"
                    ("Id", "GameKey", "IsEnabled", "MinimumBet", "MaximumBet", "Rows", "Columns", "MinesPerLine", "ReturnToPlayer", "UseCustomStepSafeChances", "StepSafeChancesJson", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    ('9a818aa4-4d31-4e1a-a8c1-88e809071f72', 'minefield', TRUE, 0.01, 1000000, 10, 5, 1, 0.95, FALSE, '', NOW(), NOW());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MinefieldGameSettings");
        }
    }
}
