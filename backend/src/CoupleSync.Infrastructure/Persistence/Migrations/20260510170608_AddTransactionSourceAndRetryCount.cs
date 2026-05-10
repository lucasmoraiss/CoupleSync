using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionSourceAndRetryCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "source",
                table: "transactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "retry_count",
                table: "import_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "retry_count",
                table: "import_jobs");
        }
    }
}
