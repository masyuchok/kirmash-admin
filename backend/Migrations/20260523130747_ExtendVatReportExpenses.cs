using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class ExtendVatReportExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Comment",
                table: "VatReportExpenses",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpenseDateUtc",
                table: "VatReportExpenses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "InvoiceContentType",
                table: "VatReportExpenses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceData",
                table: "VatReportExpenses",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceFileName",
                table: "VatReportExpenses",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "NetAmount",
                table: "VatReportExpenses",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                """
                UPDATE "VatReportExpenses"
                SET "ExpenseDateUtc" = "CreatedAtUtc",
                    "NetAmount" = GREATEST("GrossAmount" - "VatAmount", 0)
                WHERE "ExpenseDateUtc" = TIMESTAMPTZ '0001-01-01 00:00:00+00';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Comment",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "ExpenseDateUtc",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "InvoiceContentType",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "InvoiceData",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "InvoiceFileName",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "NetAmount",
                table: "VatReportExpenses");
        }
    }
}
