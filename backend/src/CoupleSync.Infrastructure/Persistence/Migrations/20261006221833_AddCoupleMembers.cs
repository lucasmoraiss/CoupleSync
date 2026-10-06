using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoupleMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new table references users and couples and an index of notification_settings is swapped: if a
            // long transaction of the previous instance holds one of them, fail fast (the migration rolls back whole).
            migrationBuilder.Sql(ForeignKeyMigrationSql.SetShortLockTimeout);

            migrationBuilder.DropIndex(
                name: "IX_notification_settings_user_id",
                table: "notification_settings");

            migrationBuilder.CreateTable(
                name: "couple_members",
                columns: table => new
                {
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    joined_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_couple_members", x => new { x.couple_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_couple_members_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_couple_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_settings_user_id_couple_id",
                table: "notification_settings",
                columns: new[] { "user_id", "couple_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_couple_members_one_owner_per_couple",
                table: "couple_members",
                column: "couple_id",
                unique: true,
                filter: "role = 'Owner'");

            migrationBuilder.CreateIndex(
                name: "IX_couple_members_user_id",
                table: "couple_members",
                column: "user_id");

            // Existing users: their single group (users.couple_id, which stays and becomes the active group) turns
            // into a membership row with the right role. See CoupleMembersMigrationSql.
            migrationBuilder.Sql(CoupleMembersMigrationSql.BackfillMemberships);
            migrationBuilder.Sql(CoupleMembersMigrationSql.MarkRegisteredOwners);
            migrationBuilder.Sql(CoupleMembersMigrationSql.GiveOwnerlessGroupsTheirOldestMember);
            migrationBuilder.Sql(CoupleMembersMigrationSql.AlignRegisteredOwnerWithOwnerRow);
            migrationBuilder.Sql(CoupleMembersMigrationSql.ExpireCodesOfGroupsWithoutMembers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Going back to one group per user: users.couple_id (the active group) is what remains of the
            // memberships; the other groups' membership rows and alert preferences are dropped.
            migrationBuilder.Sql(CoupleMembersMigrationSql.KeepNotificationSettingsOfTheActiveGroup);
            migrationBuilder.Sql(CoupleMembersMigrationSql.KeepOneNotificationSettingsRowPerUser);

            migrationBuilder.DropTable(
                name: "couple_members");

            migrationBuilder.DropIndex(
                name: "IX_notification_settings_user_id_couple_id",
                table: "notification_settings");

            migrationBuilder.CreateIndex(
                name: "IX_notification_settings_user_id",
                table: "notification_settings",
                column: "user_id",
                unique: true);
        }
    }
}
