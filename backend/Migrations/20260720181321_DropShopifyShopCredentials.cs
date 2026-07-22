using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class DropShopifyShopCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql( @"DROP TABLE IF EXISTS ""ShopifyShopCredentials"";" );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShopifyShopCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccessToken = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Shop = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopifyShopCredentials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShopifyShopCredentials_Shop",
                table: "ShopifyShopCredentials",
                column: "Shop",
                unique: true);
        }
    }
}
