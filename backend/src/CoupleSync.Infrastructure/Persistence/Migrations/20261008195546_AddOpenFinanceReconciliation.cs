using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenFinanceReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "person_decided_at_utc",
                table: "bank_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_matched_transaction_id",
                table: "bank_transactions",
                column: "matched_transaction_id");

            // The column exists since AddOpenFinanceSync and nothing ever wrote it. Should a row carry an id that is
            // no transaction, the suspicion points to nothing: it is cleared, so that the foreign key below can
            // never fail on rows that already exist. No line of the mirror is removed.
            migrationBuilder.Sql(
                """
                UPDATE bank_transactions b
                SET matched_transaction_id = NULL
                WHERE b.matched_transaction_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM transactions t WHERE t.id = b.matched_transaction_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_bank_transactions_transactions_matched_transaction_id",
                table: "bank_transactions",
                column: "matched_transaction_id",
                principalTable: "transactions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bank_transactions_transactions_matched_transaction_id",
                table: "bank_transactions");

            migrationBuilder.DropIndex(
                name: "IX_bank_transactions_matched_transaction_id",
                table: "bank_transactions");

            migrationBuilder.DropColumn(
                name: "person_decided_at_utc",
                table: "bank_transactions");
        }
    }
}
