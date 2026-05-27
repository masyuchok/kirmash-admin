using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseInvoiceTypesAndReportExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseInvoiceTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseInvoiceTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VatReportExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VatReportId = table.Column<int>(type: "integer", nullable: false),
                    ExpenseInvoiceTypeId = table.Column<int>(type: "integer", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatReportExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatReportExpenses_ExpenseInvoiceTypes_ExpenseInvoiceTypeId",
                        column: x => x.ExpenseInvoiceTypeId,
                        principalTable: "ExpenseInvoiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VatReportExpenses_VatReports_VatReportId",
                        column: x => x.VatReportId,
                        principalTable: "VatReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatReportExpenses_ExpenseInvoiceTypeId",
                table: "VatReportExpenses",
                column: "ExpenseInvoiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_VatReportExpenses_VatReportId",
                table: "VatReportExpenses",
                column: "VatReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VatReportExpenses");

            migrationBuilder.DropTable(
                name: "ExpenseInvoiceTypes");
        }
    }
}
