using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddFinanceRecurringExpenseDateRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "FinanceRecurringExpenses",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "FinanceRecurringExpenses",
                type: "date",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "FinanceRecurringExpenses"
                SET "StartDate" = CAST("CreatedAtUtc" AS date)
                WHERE "StartDate" IS NULL
                """ );

            migrationBuilder.AlterColumn<DateOnly>(
                name: "StartDate",
                table: "FinanceRecurringExpenses",
                type: "date",
                nullable: false,
                oldClrType: typeof( DateOnly ),
                oldType: "date",
                oldNullable: true );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "FinanceRecurringExpenses");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "FinanceRecurringExpenses");
        }
    }
}
