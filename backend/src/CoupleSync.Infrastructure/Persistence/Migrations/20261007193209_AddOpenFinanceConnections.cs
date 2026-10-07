using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenFinanceConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    client_id_encrypted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    client_secret_encrypted = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    client_id_hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_sync_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    history_months = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_connections_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_item_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connector_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    execution_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_items_bank_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "bank_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_items_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subtype = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    marketing_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    number_masked = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    balance_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    credit_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    available_credit_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    balance_close_date = table.Column<DateOnly>(type: "date", nullable: true),
                    balance_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    minimum_payment = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    brand = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    sync_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_bank_accounts_bank_items_item_id",
                        column: x => x.item_id,
                        principalTable: "bank_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bank_accounts_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_couple_id",
                table: "bank_accounts",
                column: "couple_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_item_id",
                table: "bank_accounts",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_pluggy_account_id",
                table: "bank_accounts",
                column: "pluggy_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_connections_couple_id_user_id",
                table: "bank_connections",
                columns: new[] { "couple_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_connections_user_id",
                table: "bank_connections",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_items_connection_id",
                table: "bank_items",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_items_couple_id",
                table: "bank_items",
                column: "couple_id");

            migrationBuilder.CreateIndex(
                name: "IX_bank_items_pluggy_item_id",
                table: "bank_items",
                column: "pluggy_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "bank_items");

            migrationBuilder.DropTable(
                name: "bank_connections");
        }
    }
}
