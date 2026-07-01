using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryProductSaleShopifyVariantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryProductSales_ShopifyProductId",
                table: "InventoryProductSales");

            migrationBuilder.AddColumn<string>(
                name: "ShopifyVariantId",
                table: "InventoryProductSales",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProductSales_ShopifyProductId_ShopifyVariantId",
                table: "InventoryProductSales",
                columns: new[] { "ShopifyProductId", "ShopifyVariantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryProductSales_ShopifyProductId_ShopifyVariantId",
                table: "InventoryProductSales");

            migrationBuilder.DropColumn(
                name: "ShopifyVariantId",
                table: "InventoryProductSales");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryProductSales_ShopifyProductId",
                table: "InventoryProductSales",
                column: "ShopifyProductId",
                unique: true);
        }
    }
}
