using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class ExtendVatReportsForPolandGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Document",
                table: "VatReports",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "VatReports",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string[]>(
                name: "ShopifyOrderIds",
                table: "VatReports",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "VatReports",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "VatReportRows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VatReportId = table.Column<int>(type: "integer", nullable: false),
                    ShopifyOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrderNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrderDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    ShippingGrossAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    ShippingNetAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatReportRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatReportRows_VatReports_VatReportId",
                        column: x => x.VatReportId,
                        principalTable: "VatReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatReportRows_VatReportId",
                table: "VatReportRows",
                column: "VatReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VatReportRows");

            migrationBuilder.DropColumn(
                name: "Document",
                table: "VatReports");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "VatReports");

            migrationBuilder.DropColumn(
                name: "ShopifyOrderIds",
                table: "VatReports");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "VatReports");
        }
    }
}
