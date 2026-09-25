using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    [Migration("20260916120000_AddSupplierPriceListUrl")]
    public class AddSupplierPriceListUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Suppliers"
                ADD COLUMN IF NOT EXISTS "PriceListUrl" text NULL;
                """
            );
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Suppliers"
                DROP COLUMN IF EXISTS "PriceListUrl";
                """
            );
        }
    }
}
