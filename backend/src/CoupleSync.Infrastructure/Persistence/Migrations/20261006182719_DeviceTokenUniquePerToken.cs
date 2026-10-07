using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceTokenUniquePerToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A token belongs to a single user. Production may already hold the same token under several
            // users (a phone that switched accounts), which would make the unique index fail: keep the
            // newest row of each token and drop the rest first. See DeviceTokenDeduplicationSql.
            migrationBuilder.Sql(DeviceTokenDeduplicationSql.KeepNewestRowPerToken);

            migrationBuilder.CreateIndex(
                name: "IX_device_tokens_token",
                table: "device_tokens",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_device_tokens_token",
                table: "device_tokens");
        }
    }
}
