using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddVatAutoFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VatAutoFinanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FinancePersonId = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatAutoFinanceSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VatPeriodFinancePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeriodYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: false),
                    FinanceMovementId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatPeriodFinancePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VatPeriodFinancePayments_FinanceMovements_FinanceMovementId",
                        column: x => x.FinanceMovementId,
                        principalTable: "FinanceMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VatPeriodFinancePayments_FinanceMovementId",
                table: "VatPeriodFinancePayments",
                column: "FinanceMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_VatPeriodFinancePayments_PeriodYear_PeriodMonth",
                table: "VatPeriodFinancePayments",
                columns: new[] { "PeriodYear", "PeriodMonth" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VatAutoFinanceSettings");

            migrationBuilder.DropTable(
                name: "VatPeriodFinancePayments");
        }
    }
}
