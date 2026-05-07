using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkinMarket.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceAwarePriceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency",
                table: "PriceSnapshots");

            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "PriceSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BestAskUsd",
                table: "PriceSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BestBidUsd",
                table: "PriceSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConfidenceScore",
                table: "PriceSnapshots",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "FxObservedAtUtc",
                table: "PriceSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "PriceSnapshots",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ObservedAtUtc",
                table: "PriceSnapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "OriginalCurrency",
                table: "PriceSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalPrice",
                table: "PriceSnapshots",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceType",
                table: "PriceSnapshots",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "unavailable");

            migrationBuilder.AddColumn<decimal>(
                name: "PriceUsd",
                table: "PriceSnapshots",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "PriceSnapshots",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "PriceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RawPayloadHash",
                table: "PriceSnapshots",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SalesCount",
                table: "PriceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceItemId",
                table: "PriceSnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TtlSeconds",
                table: "PriceSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VariantKey",
                table: "PriceSnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Volume",
                table: "PriceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "PriceSnapshots"
                SET
                    "PriceUsd" = "Price",
                    "OriginalPrice" = "Price",
                    "OriginalCurrency" = "Currency",
                    "FxRate" = CASE WHEN upper("Currency") = 'USD' THEN 1 ELSE NULL END,
                    "ObservedAtUtc" = "UpdatedAtUtc",
                    "TtlSeconds" = GREATEST(60, CAST(EXTRACT(EPOCH FROM ("ExpiresAtUtc" - "UpdatedAtUtc")) AS integer)),
                    "PriceType" = CASE
                        WHEN "HasPrice" = false THEN 'unavailable'
                        WHEN "Source" = 'Steam' THEN 'reference_external'
                        WHEN "Source" = 'CSFloat' THEN 'reference_external'
                        WHEN "IsEstimated" = true THEN 'suggested'
                        ELSE 'reference_external'
                    END,
                    "IsEstimated" = CASE
                        WHEN "HasPrice" = false THEN false
                        WHEN "Source" IN ('Steam', 'CSFloat') THEN true
                        ELSE "IsEstimated"
                    END,
                    "ConfidenceScore" = CASE
                        WHEN "HasPrice" = false THEN 0
                        WHEN "Source" = 'Steam' THEN 0.35
                        WHEN "Source" = 'CSFloat' THEN 0.30
                        WHEN "IsEstimated" = true THEN 0.40
                        ELSE 0.45
                    END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots",
                columns: new[] { "AppId", "MarketHashName", "Currency", "Source", "PriceType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_ExpiresAtUtc",
                table: "PriceSnapshots",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency_Source_PriceTy~",
                table: "PriceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PriceSnapshots_ExpiresAtUtc",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "BestAskUsd",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "BestBidUsd",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "ConfidenceScore",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "FxObservedAtUtc",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "ObservedAtUtc",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "OriginalCurrency",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "OriginalPrice",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "PriceType",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "PriceUsd",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "RawPayloadHash",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "SalesCount",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "SourceItemId",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "TtlSeconds",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "VariantKey",
                table: "PriceSnapshots");

            migrationBuilder.DropColumn(
                name: "Volume",
                table: "PriceSnapshots");

            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "PriceSnapshots",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_AppId_MarketHashName_Currency",
                table: "PriceSnapshots",
                columns: new[] { "AppId", "MarketHashName", "Currency" },
                unique: true);
        }
    }
}
