using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoupleOwnerAndJoinCodeExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "join_code",
                table: "couples",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(6)",
                oldMaxLength: 6);

            migrationBuilder.AddColumn<DateTime>(
                name: "join_code_expires_at_utc",
                table: "couples",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                table: "couples",
                type: "uuid",
                nullable: true);

            // Groups created before this migration: the oldest member becomes the owner and the current
            // invite code gets 7 days of validity counted from now. See CoupleGroupManagementSql.
            migrationBuilder.Sql(CoupleGroupManagementSql.SetOldestMemberAsOwner);
            migrationBuilder.Sql(CoupleGroupManagementSql.GiveExistingCodesSevenDays(DateTime.UtcNow));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "join_code_expires_at_utc",
                table: "couples");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                table: "couples");

            migrationBuilder.AlterColumn<string>(
                name: "join_code",
                table: "couples",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8);
        }
    }
}
