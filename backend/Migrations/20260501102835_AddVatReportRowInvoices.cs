using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatReportRowInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvoiceContentType",
                table: "VatReportRows",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "InvoiceData",
                table: "VatReportRows",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceFileName",
                table: "VatReportRows",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvoiceContentType",
                table: "VatReportRows");

            migrationBuilder.DropColumn(
                name: "InvoiceData",
                table: "VatReportRows");

            migrationBuilder.DropColumn(
                name: "InvoiceFileName",
                table: "VatReportRows");
        }
    }
}
