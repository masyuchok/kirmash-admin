using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddKirmaBukinistkaOfferStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers"
                    ADD COLUMN IF NOT EXISTS "Status" character varying(32) NOT NULL DEFAULT 'Pending';
                UPDATE "KirmaBukinistkaOffers"
                    SET "Status" = 'Pending'
                    WHERE "Status" IS NULL OR TRIM("Status") = '';
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "KirmaBukinistkaOffers" DROP COLUMN IF EXISTS "Status";
                """ );
        }
    }
}
