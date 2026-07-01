using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryProductSalePeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql( "DELETE FROM \"InventoryProductSales\";" );

            migrationBuilder.DropIndex(
                name: "IX_InventoryProductSales_ShopifyProductId_ShopifyVariantId",
                table: "InventoryProductSales");

            migrationBuilder.AddColumn<int>(
                name: "PeriodMonth",
                table: "InventoryProductSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PeriodYear",
                table: "InventoryProductSales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProductSales_PeriodYear_PeriodMonth_ShopifyProduct~",
                table: "InventoryProductSales",
                columns: new[] { "PeriodYear", "PeriodMonth", "ShopifyProductId", "ShopifyVariantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryProductSales_PeriodYear_PeriodMonth_ShopifyProduct~",
                table: "InventoryProductSales");

            migrationBuilder.DropColumn(
                name: "PeriodMonth",
                table: "InventoryProductSales");

            migrationBuilder.DropColumn(
                name: "PeriodYear",
                table: "InventoryProductSales");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProductSales_ShopifyProductId_ShopifyVariantId",
                table: "InventoryProductSales",
                columns: new[] { "ShopifyProductId", "ShopifyVariantId" },
                unique: true);
        }
    }
}
