using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeSourcesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "income_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    couple_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_income_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_income_sources_couples_couple_id",
                        column: x => x.couple_id,
                        principalTable: "couples",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_income_sources_couple_id_month",
                table: "income_sources",
                columns: new[] { "couple_id", "month" });

            migrationBuilder.CreateIndex(
                name: "IX_income_sources_couple_id_user_id_month_name",
                table: "income_sources",
                columns: new[] { "couple_id", "user_id", "month", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_income_sources_user_id_month",
                table: "income_sources",
                columns: new[] { "user_id", "month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "income_sources");
        }
    }
}
