using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatReportUnpaidAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VatReportUnpaidAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SalePeriodYear = table.Column<int>(type: "integer", nullable: false),
                    SalePeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    ShopifyProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProductTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    VatReportExpenseId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatReportUnpaidAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatReportUnpaidAllocations_VatReportExpenses_VatReportExpen~",
                        column: x => x.VatReportExpenseId,
                        principalTable: "VatReportExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatReportUnpaidAllocations_SalePeriodYear_SalePeriodMonth_S~",
                table: "VatReportUnpaidAllocations",
                columns: new[] { "SalePeriodYear", "SalePeriodMonth", "ShopifyProductId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VatReportUnpaidAllocations_VatReportExpenseId",
                table: "VatReportUnpaidAllocations",
                column: "VatReportExpenseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VatReportUnpaidAllocations");
        }
    }
}
