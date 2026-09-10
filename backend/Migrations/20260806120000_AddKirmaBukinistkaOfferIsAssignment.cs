using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260806120000_AddKirmaBukinistkaOfferIsAssignment" )]
    public class AddKirmaBukinistkaOfferIsAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "IsAssignment" boolean NOT NULL DEFAULT false;
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers" DROP COLUMN IF EXISTS "IsAssignment";
                """ );
        }
    }
}
