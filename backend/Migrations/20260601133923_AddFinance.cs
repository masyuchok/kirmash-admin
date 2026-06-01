using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancePersons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancePersons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FinanceRecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceRecurringExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinanceRecurringExpenses_FinancePersons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "FinancePersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FinanceMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsFromRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    RecurringExpenseId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinanceMovements_FinancePersons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "FinancePersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FinanceMovements_FinanceRecurringExpenses_RecurringExpenseId",
                        column: x => x.RecurringExpenseId,
                        principalTable: "FinanceRecurringExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FinanceRecurringApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecurringExpenseId = table.Column<int>(type: "integer", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Month = table.Column<int>(type: "integer", nullable: false),
                    MovementId = table.Column<int>(type: "integer", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceRecurringApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinanceRecurringApplications_FinanceMovements_MovementId",
                        column: x => x.MovementId,
                        principalTable: "FinanceMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FinanceRecurringApplications_FinanceRecurringExpenses_Recur~",
                        column: x => x.RecurringExpenseId,
                        principalTable: "FinanceRecurringExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceMovements_PersonId",
                table: "FinanceMovements",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceMovements_RecurringExpenseId",
                table: "FinanceMovements",
                column: "RecurringExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_FinancePersons_Name",
                table: "FinancePersons",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinanceRecurringApplications_MovementId",
                table: "FinanceRecurringApplications",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_FinanceRecurringApplications_RecurringExpenseId_Year_Month",
                table: "FinanceRecurringApplications",
                columns: new[] { "RecurringExpenseId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinanceRecurringExpenses_PersonId",
                table: "FinanceRecurringExpenses",
                column: "PersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinanceRecurringApplications");

            migrationBuilder.DropTable(
                name: "FinanceMovements");

            migrationBuilder.DropTable(
                name: "FinanceRecurringExpenses");

            migrationBuilder.DropTable(
                name: "FinancePersons");
        }
    }
}
