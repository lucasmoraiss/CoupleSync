using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringStreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recurring_streams",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    variable_amount = table.Column<bool>(type: "boolean", nullable: false),
                    cadence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    median_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    last_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    previous_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    annual_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    missed_count = table.Column<int>(type: "integer", nullable: false),
                    first_seen_local = table.Column<DateOnly>(type: "date", nullable: false),
                    last_seen_local = table.Column<DateOnly>(type: "date", nullable: false),
                    next_expected_local = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    flags = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    confidence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: true),
                    installment_total = table.Column<int>(type: "integer", nullable: true),
                    remaining_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    end_month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    user_override = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    override_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    override_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    detected_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_streams", x => x.id);
                    table.ForeignKey(
                        name: "FK_recurring_streams_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recurring_stream_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_stream_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_recurring_stream_items_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recurring_stream_items_recurring_streams_stream_id",
                        column: x => x.stream_id,
                        principalTable: "recurring_streams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recurring_stream_items_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recurring_stream_items_couple_id",
                table: "recurring_stream_items",
                column: "couple_id");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_stream_items_stream_id_transaction_id",
                table: "recurring_stream_items",
                columns: new[] { "stream_id", "transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_stream_items_transaction_id",
                table: "recurring_stream_items",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_streams_couple_id_merchant_key_kind_cadence",
                table: "recurring_streams",
                columns: new[] { "couple_id", "merchant_key", "kind", "cadence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_streams_couple_id_status",
                table: "recurring_streams",
                columns: new[] { "couple_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recurring_stream_items");

            migrationBuilder.DropTable(
                name: "recurring_streams");
        }
    }
}
