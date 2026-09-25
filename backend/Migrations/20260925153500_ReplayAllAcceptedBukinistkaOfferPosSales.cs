using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Replays the accepted Bukinistka→Kirma period once more after POS reads
    /// became paginated. The previous replay could only see the first 500 orders.
    /// Existing Odoo line ids make this replay idempotent.
    /// </summary>
    [Migration( "20260925153500_ReplayAllAcceptedBukinistkaOfferPosSales" )]
    public class ReplayAllAcceptedBukinistkaOfferPosSales : Migration
    {
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    backfill_from timestamp with time zone;
                BEGIN
                    SELECT MIN(COALESCE("AcceptedAtUtc", "CreatedAtUtc"))
                    INTO backfill_from
                    FROM "KirmaBukinistkaOffers"
                    WHERE "Status" = 'Accepted'
                      AND "Direction" = 'BukinistkaToKirma'
                      AND COALESCE("IsAssignment", false) = false
                      AND "OdooProductId" IS NOT NULL
                      AND "OdooProductId" > 0
                      AND COALESCE("ShopifyProductId", '') <> '';

                    IF backfill_from IS NOT NULL THEN
                        UPDATE "KirmaBukinistkaPosSyncStates"
                        SET "LastSyncedAtUtc" = backfill_from,
                            "LastProcessedOrderId" = NULL;
                    END IF;
                END $$;
                """ );
        }

        protected override void Down( MigrationBuilder migrationBuilder )
        {
            // Data cursor rewind is intentionally not reversible.
        }
    }
}
