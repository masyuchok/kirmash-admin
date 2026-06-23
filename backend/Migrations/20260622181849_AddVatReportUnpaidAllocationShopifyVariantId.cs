using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatReportUnpaidAllocationShopifyVariantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VatReportUnpaidAllocations_SalePeriodYear_SalePeriodMonth_S~",
                table: "VatReportUnpaidAllocations");

            migrationBuilder.AddColumn<string>(
                name: "ShopifyVariantId",
                table: "VatReportUnpaidAllocations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_VatReportUnpaidAllocations_SalePeriodYear_SalePeriodMonth_S~",
                table: "VatReportUnpaidAllocations",
                columns: new[] { "SalePeriodYear", "SalePeriodMonth", "ShopifyProductId", "ShopifyVariantId", "SupplierId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VatReportUnpaidAllocations_SalePeriodYear_SalePeriodMonth_S~",
                table: "VatReportUnpaidAllocations");

            migrationBuilder.DropColumn(
                name: "ShopifyVariantId",
                table: "VatReportUnpaidAllocations");

            migrationBuilder.CreateIndex(
                name: "IX_VatReportUnpaidAllocations_SalePeriodYear_SalePeriodMonth_S~",
                table: "VatReportUnpaidAllocations",
                columns: new[] { "SalePeriodYear", "SalePeriodMonth", "ShopifyProductId", "SupplierId" },
                unique: true);
        }
    }
}
