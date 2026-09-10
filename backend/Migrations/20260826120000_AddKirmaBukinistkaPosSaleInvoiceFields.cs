using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260826120000_AddKirmaBukinistkaPosSaleInvoiceFields" )]
    public class AddKirmaBukinistkaPosSaleInvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaPosSales"
                    ADD COLUMN IF NOT EXISTS "IsInvoiced" boolean NOT NULL DEFAULT false;
                ALTER TABLE "KirmaBukinistkaPosSales"
                    ADD COLUMN IF NOT EXISTS "InvoicedAtUtc" timestamp with time zone NULL;
                ALTER TABLE "KirmaBukinistkaPosSales"
                    ADD COLUMN IF NOT EXISTS "VatReportRowId" integer NULL;
                CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPosSales_IsInvoiced"
                    ON "KirmaBukinistkaPosSales" ("IsInvoiced");
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_KirmaBukinistkaPosSales_IsInvoiced";
                ALTER TABLE "KirmaBukinistkaPosSales" DROP COLUMN IF EXISTS "VatReportRowId";
                ALTER TABLE "KirmaBukinistkaPosSales" DROP COLUMN IF EXISTS "InvoicedAtUtc";
                ALTER TABLE "KirmaBukinistkaPosSales" DROP COLUMN IF EXISTS "IsInvoiced";
                """ );
        }
    }
}
