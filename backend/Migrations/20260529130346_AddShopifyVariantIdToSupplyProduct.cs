using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddShopifyVariantIdToSupplyProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplyProducts_SupplyId_ShopifyProductId",
                table: "SupplyProducts");

            migrationBuilder.AddColumn<string>(
                name: "ShopifyVariantId",
                table: "SupplyProducts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyProducts_SupplyId_ShopifyProductId_ShopifyVariantId",
                table: "SupplyProducts",
                columns: new[] { "SupplyId", "ShopifyProductId", "ShopifyVariantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplyProducts_SupplyId_ShopifyProductId_ShopifyVariantId",
                table: "SupplyProducts");

            migrationBuilder.DropColumn(
                name: "ShopifyVariantId",
                table: "SupplyProducts");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyProducts_SupplyId_ShopifyProductId",
                table: "SupplyProducts",
                columns: new[] { "SupplyId", "ShopifyProductId" },
                unique: true);
        }
    }
}
