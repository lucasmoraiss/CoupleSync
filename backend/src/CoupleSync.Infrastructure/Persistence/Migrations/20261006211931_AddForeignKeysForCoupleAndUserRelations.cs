using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignKeysForCoupleAndUserRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adding a key needs a strong lock on both tables. If a long transaction of the previous instance holds
            // one, fail fast (the migration rolls back whole) instead of queueing and blocking every writer behind us.
            migrationBuilder.Sql(ForeignKeyMigrationSql.SetShortLockTimeout);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_user_id",
                table: "transactions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_event_ingests_user_id",
                table: "transaction_event_ingests",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_user_id",
                table: "notification_events",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_import_jobs_user_id",
                table: "import_jobs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_goals_created_by_user_id",
                table: "goals",
                column: "created_by_user_id");

            // Derived data only (tokens, settings, events): rows that point to a group/user that no longer exists
            // are useless and would make the keys fail, so they go first. See ForeignKeyMigrationSql.
            foreach (var (table, column, principal) in ForeignKeyMigrationSql.DerivedKeys)
            {
                migrationBuilder.Sql(ForeignKeyMigrationSql.DeleteOrphans(table, column, principal));
            }

            migrationBuilder.AddForeignKey(
                name: "FK_device_tokens_couples_couple_id",
                table: "device_tokens",
                column: "couple_id",
                principalTable: "couples",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_device_tokens_users_user_id",
                table: "device_tokens",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_events_couples_couple_id",
                table: "notification_events",
                column: "couple_id",
                principalTable: "couples",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_events_users_user_id",
                table: "notification_events",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_settings_couples_couple_id",
                table: "notification_settings",
                column: "couple_id",
                principalTable: "couples",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_settings_users_user_id",
                table: "notification_settings",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // The users' own data (transactions, ingest events, goals, incomes, import jobs) is never deleted to
            // satisfy a key: the key is created NOT VALID and validated only if no orphan row exists.
            foreach (var (table, column, principal) in ForeignKeyMigrationSql.UserDataKeys)
            {
                migrationBuilder.Sql(ForeignKeyMigrationSql.AddNotValidThenValidateIfPossible(table, column, principal));
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_device_tokens_couples_couple_id",
                table: "device_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_device_tokens_users_user_id",
                table: "device_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_goals_couples_couple_id",
                table: "goals");

            migrationBuilder.DropForeignKey(
                name: "FK_goals_users_created_by_user_id",
                table: "goals");

            migrationBuilder.DropForeignKey(
                name: "FK_import_jobs_couples_couple_id",
                table: "import_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_import_jobs_users_user_id",
                table: "import_jobs");

            migrationBuilder.DropForeignKey(
                name: "FK_income_sources_users_user_id",
                table: "income_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_events_couples_couple_id",
                table: "notification_events");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_events_users_user_id",
                table: "notification_events");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_settings_couples_couple_id",
                table: "notification_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_settings_users_user_id",
                table: "notification_settings");

            migrationBuilder.DropForeignKey(
                name: "FK_transaction_event_ingests_couples_couple_id",
                table: "transaction_event_ingests");

            migrationBuilder.DropForeignKey(
                name: "FK_transaction_event_ingests_users_user_id",
                table: "transaction_event_ingests");

            migrationBuilder.DropForeignKey(
                name: "FK_transactions_couples_couple_id",
                table: "transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_transactions_users_user_id",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "IX_transactions_user_id",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "IX_transaction_event_ingests_user_id",
                table: "transaction_event_ingests");

            migrationBuilder.DropIndex(
                name: "IX_notification_events_user_id",
                table: "notification_events");

            migrationBuilder.DropIndex(
                name: "IX_import_jobs_user_id",
                table: "import_jobs");

            migrationBuilder.DropIndex(
                name: "IX_goals_created_by_user_id",
                table: "goals");
        }
    }
}
