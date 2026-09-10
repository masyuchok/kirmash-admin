using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260827120000_AddKirmaBukinistkaOfferAcceptedAtUtc" )]
    public class AddKirmaBukinistkaOfferAcceptedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "AcceptedAtUtc" timestamp with time zone NULL;
                UPDATE "KirmaBukinistkaOffers"
                    SET "AcceptedAtUtc" = "CreatedAtUtc"
                    WHERE "Status" = 'Accepted' AND "AcceptedAtUtc" IS NULL;
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers" DROP COLUMN IF EXISTS "AcceptedAtUtc";
                """ );
        }
    }
}
