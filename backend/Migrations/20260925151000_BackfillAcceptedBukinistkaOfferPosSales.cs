using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Rewinds the POS cursor once so sales skipped for already accepted
    /// Bukinistka→Kirma offers are replayed by the fixed idempotent synchronizer.
    /// </summary>
    [Migration( "20260925151000_BackfillAcceptedBukinistkaOfferPosSales" )]
    public class BackfillAcceptedBukinistkaOfferPosSales : Migration
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
                        IF EXISTS (SELECT 1 FROM "KirmaBukinistkaPosSyncStates") THEN
                            UPDATE "KirmaBukinistkaPosSyncStates"
                            SET "LastSyncedAtUtc" =
                                    LEAST(
                                        COALESCE("LastSyncedAtUtc", backfill_from),
                                        backfill_from),
                                "LastProcessedOrderId" = NULL;
                        ELSE
                            INSERT INTO "KirmaBukinistkaPosSyncStates"
                                ("LastSyncedAtUtc", "LastProcessedOrderId")
                            VALUES (backfill_from, NULL);
                        END IF;
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
