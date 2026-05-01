using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatReportRowItemsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VatReportRowItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VatReportRowId = table.Column<int>(type: "integer", nullable: false),
                    ProductTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    AssignedVatRatePercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    AssignmentReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatReportRowItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatReportRowItems_VatReportRows_VatReportRowId",
                        column: x => x.VatReportRowId,
                        principalTable: "VatReportRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatReportRowItems_VatReportRowId",
                table: "VatReportRowItems",
                column: "VatReportRowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VatReportRowItems");
        }
    }
}
