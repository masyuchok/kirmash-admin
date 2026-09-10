using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    [Migration( "20260824190000_AddKirmaBukinistkaPendingOfferSaleDeductions" )]
    public class AddKirmaBukinistkaPendingOfferSaleDeductions : Migration
    {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "KirmaBukinistkaPendingOfferSaleDeductions" (
                    "Id" serial PRIMARY KEY,
                    "OfferId" integer NOT NULL
                        REFERENCES "KirmaBukinistkaOffers" ("Id") ON DELETE CASCADE,
                    "Source" character varying(32) NOT NULL,
                    "SourceKey" character varying(128) NOT NULL,
                    "Quantity" integer NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPendingOfferSaleDeductions_Source_SourceKey_OfferId"
                    ON "KirmaBukinistkaPendingOfferSaleDeductions" ("Source", "SourceKey", "OfferId");

                CREATE INDEX IF NOT EXISTS "IX_KirmaBukinistkaPendingOfferSaleDeductions_OfferId"
                    ON "KirmaBukinistkaPendingOfferSaleDeductions" ("OfferId");
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder )
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "KirmaBukinistkaPendingOfferSaleDeductions";
                """ );
        }
    }
}
