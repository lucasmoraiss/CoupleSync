using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_transactions_couple_id_category",
                table: "transactions",
                columns: new[] { "couple_id", "category" });

            migrationBuilder.CreateIndex(
                name: "IX_transactions_couple_id_event_timestamp_utc",
                table: "transactions",
                columns: new[] { "couple_id", "event_timestamp_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_transaction_event_ingests_couple_id_created_at_utc",
                table: "transaction_event_ingests",
                columns: new[] { "couple_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transactions_couple_id_category",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "IX_transactions_couple_id_event_timestamp_utc",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "IX_transaction_event_ingests_couple_id_created_at_utc",
                table: "transaction_event_ingests");
        }
    }
}
