using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260720193000_AddKirmaBukinistkaOfferAcceptFields" )]
    public class AddKirmaBukinistkaOfferAcceptFields : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            // Separate statements: more reliable across Postgres versions than multi-ADD.
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "OdooProductId" integer NULL;
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "OdooQuantityBeforeAccept" integer NULL;
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "AcceptedListPrice" numeric(18,2) NULL;
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    DROP COLUMN IF EXISTS "OdooProductId";
                ALTER TABLE "KirmaBukinistkaOffers"
                    DROP COLUMN IF EXISTS "OdooQuantityBeforeAccept";
                ALTER TABLE "KirmaBukinistkaOffers"
                    DROP COLUMN IF EXISTS "AcceptedListPrice";
                """ );
        }
    }
}
