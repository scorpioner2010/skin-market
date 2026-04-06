using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSteamProfileFieldsToAppUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "AppUsers",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonaName",
                table: "AppUsers",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "PersonaName",
                table: "AppUsers");
        }
    }
}
