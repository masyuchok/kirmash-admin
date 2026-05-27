using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatReportExpenseProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "VatReportExpenses",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VatReportExpenseProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VatReportExpenseId = table.Column<int>(type: "integer", nullable: false),
                    ShopifyProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProductTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatReportExpenseProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatReportExpenseProducts_VatReportExpenses_VatReportExpense~",
                        column: x => x.VatReportExpenseId,
                        principalTable: "VatReportExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatReportExpenses_SupplierId",
                table: "VatReportExpenses",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_VatReportExpenseProducts_VatReportExpenseId",
                table: "VatReportExpenseProducts",
                column: "VatReportExpenseId");

            migrationBuilder.AddForeignKey(
                name: "FK_VatReportExpenses_Suppliers_SupplierId",
                table: "VatReportExpenses",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VatReportExpenses_Suppliers_SupplierId",
                table: "VatReportExpenses");

            migrationBuilder.DropTable(
                name: "VatReportExpenseProducts");

            migrationBuilder.DropIndex(
                name: "IX_VatReportExpenses_SupplierId",
                table: "VatReportExpenses");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "VatReportExpenses");
        }
    }
}
