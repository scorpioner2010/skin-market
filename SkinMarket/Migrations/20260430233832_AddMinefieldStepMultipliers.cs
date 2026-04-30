using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SkinMarket.Data;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260430233832_AddMinefieldStepMultipliers")]
    public partial class AddMinefieldStepMultipliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StepMultipliersJson",
                table: "MinefieldGameSettings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "UseCustomStepMultipliers",
                table: "MinefieldGameSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StepMultipliersJson",
                table: "MinefieldGameSettings");

            migrationBuilder.DropColumn(
                name: "UseCustomStepMultipliers",
                table: "MinefieldGameSettings");
        }
    }
}
