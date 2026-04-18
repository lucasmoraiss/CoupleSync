using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionGoalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "goal_id",
                table: "transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_goal_id",
                table: "transactions",
                column: "goal_id");

            migrationBuilder.AddForeignKey(
                name: "FK_transactions_goals_goal_id",
                table: "transactions",
                column: "goal_id",
                principalTable: "goals",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_transactions_goals_goal_id",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "IX_transactions_goal_id",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "goal_id",
                table: "transactions");
        }
    }
}
