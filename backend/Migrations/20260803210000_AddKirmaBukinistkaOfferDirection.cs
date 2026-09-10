using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260803210000_AddKirmaBukinistkaOfferDirection" )]
    public class AddKirmaBukinistkaOfferDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "Direction" character varying(32) NOT NULL DEFAULT 'KirmaToBukinistka';
                UPDATE "KirmaBukinistkaOffers"
                    SET "Direction" = 'KirmaToBukinistka'
                    WHERE "Direction" IS NULL OR TRIM("Direction") = '';
                CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_ShopifyProductId"
                    ON "KirmaBukinistkaOffers" ("Direction", "Status", "ShopifyProductId");
                CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_OdooProductId"
                    ON "KirmaBukinistkaOffers" ("Direction", "Status", "OdooProductId");
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_ShopifyProductId";
                DROP INDEX IF EXISTS "IX_KirmaBukinistkaOffers_Direction_Status_OdooProductId";
                ALTER TABLE "KirmaBukinistkaOffers" DROP COLUMN IF EXISTS "Direction";
                """ );
        }
    }
}
