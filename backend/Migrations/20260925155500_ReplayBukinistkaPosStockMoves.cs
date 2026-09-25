using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <summary>
    /// Replays accepted Bukinistka→Kirma availability after adding completed
    /// WH/POS stock moves as a fallback source.
    /// </summary>
    [Migration( "20260925155500_ReplayBukinistkaPosStockMoves" )]
    public class ReplayBukinistkaPosStockMoves : Migration
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
                        SET "LastSyncedAtUtc" = backfill_from - INTERVAL '4 hours',
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
