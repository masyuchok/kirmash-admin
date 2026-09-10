using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260824180000_AddKirmaBukinistkaOfferPeerPriceChangePending" )]
    public class AddKirmaBukinistkaOfferPeerPriceChangePending : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "PeerPriceChangePending" boolean NOT NULL DEFAULT false;
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers" DROP COLUMN IF EXISTS "PeerPriceChangePending";
                """ );
        }
    }
}
