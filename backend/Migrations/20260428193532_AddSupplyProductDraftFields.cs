using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplyProductDraftFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MarginPercent",
                table: "SupplyProducts",
                type: "numeric(7,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "SupplyProducts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SalePrice",
                table: "SupplyProducts",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SupplierPrice",
                table: "SupplyProducts",
                type: "numeric(12,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarginPercent",
                table: "SupplyProducts");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "SupplyProducts");

            migrationBuilder.DropColumn(
                name: "SalePrice",
                table: "SupplyProducts");

            migrationBuilder.DropColumn(
                name: "SupplierPrice",
                table: "SupplyProducts");
        }
    }
}
