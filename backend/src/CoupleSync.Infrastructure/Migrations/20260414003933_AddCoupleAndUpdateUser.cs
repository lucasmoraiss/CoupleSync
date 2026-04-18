using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCoupleAndUpdateUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "couple_joined_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "couples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    join_code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_couples", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_couple_id",
                table: "users",
                column: "couple_id");

            migrationBuilder.CreateIndex(
                name: "IX_couples_join_code",
                table: "couples",
                column: "join_code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_users_couples_couple_id",
                table: "users",
                column: "couple_id",
                principalTable: "couples",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_couples_couple_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "couples");

            migrationBuilder.DropIndex(
                name: "IX_users_couple_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "couple_joined_at_utc",
                table: "users");
        }
    }
}
