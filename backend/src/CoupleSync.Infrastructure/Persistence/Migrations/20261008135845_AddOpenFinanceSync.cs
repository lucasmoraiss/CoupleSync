using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenFinanceSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    triggered_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    force_item_update = table.Column<bool>(type: "boolean", nullable: false),
                    ai_categorization_consent = table.Column<bool>(type: "boolean", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    transactions_new = table.Column<int>(type: "integer", nullable: false),
                    transactions_updated = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_sync_runs_bank_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sync_runs_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    description_raw = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    pluggy_category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    pluggy_category_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    merchant_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    merchant_cnpj = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    merchant_category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    payment_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    installment_number = table.Column<int>(type: "integer", nullable: true),
                    installment_total = table.Column<int>(type: "integer", nullable: true),
                    bill_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    review_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    linked_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_income_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matched_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    suggested_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reviewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_transactions", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_transactions_bank_accounts_bank_account_id",
                        column: x => x.bank_account_id,
                        principalTable: "bank_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_transactions_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_transactions_income_sources_linked_income_source_id",
                        column: x => x.linked_income_source_id,
                        principalTable: "income_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_transactions_linked_transaction_id",
                        column: x => x.linked_transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bank_transactions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_bank_account_id",
                table: "bank_transactions",
                column: "bank_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_couple_id_local_date",
                table: "bank_transactions",
                columns: new[] { "couple_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_couple_id_review_state",
                table: "bank_transactions",
                columns: new[] { "couple_id", "review_state" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_linked_income_source_id",
                table: "bank_transactions",
                column: "linked_income_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_linked_transaction_id",
                table: "bank_transactions",
                column: "linked_transaction_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_pluggy_transaction_id",
                table: "bank_transactions",
                column: "pluggy_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_sync_run_id",
                table: "bank_transactions",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_transactions_user_id",
                table: "bank_transactions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_connection_id_created_at_utc",
                table: "sync_runs",
                columns: new[] { "connection_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_couple_id",
                table: "sync_runs",
                column: "couple_id");

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_one_open_per_connection",
                table: "sync_runs",
                column: "connection_id",
                unique: true,
                filter: "status IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_status",
                table: "sync_runs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_transactions");

            migrationBuilder.DropTable(
                name: "sync_runs");
        }
    }
}
