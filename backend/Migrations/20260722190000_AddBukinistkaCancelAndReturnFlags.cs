using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    [Migration( "20260722190000_AddBukinistkaCancelAndReturnFlags" )]
    public class AddBukinistkaCancelAndReturnFlags : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaPosSales"
                    ADD COLUMN IF NOT EXISTS "IsReversed" boolean NOT NULL DEFAULT false;
                ALTER TABLE "KirmaBukinistkaPosSales"
                    ADD COLUMN IF NOT EXISTS "IsReturn" boolean NOT NULL DEFAULT false;

                ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs"
                    ADD COLUMN IF NOT EXISTS "IsCancelled" boolean NOT NULL DEFAULT false;
                ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs"
                    ADD COLUMN IF NOT EXISTS "CancelledAtUtc" timestamp with time zone NULL;
                """ );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaPosSales" DROP COLUMN IF EXISTS "IsReversed";
                ALTER TABLE "KirmaBukinistkaPosSales" DROP COLUMN IF EXISTS "IsReturn";
                ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs" DROP COLUMN IF EXISTS "IsCancelled";
                ALTER TABLE "KirmaBukinistkaShopifyDeliverySyncs" DROP COLUMN IF EXISTS "CancelledAtUtc";
                """ );
        }
    }
}
