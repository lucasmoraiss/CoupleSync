using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoupleSync.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCategoriesAndCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only migration (no schema change): categories converge to the canonical keys, colliding
            // allocations of one plan are summed, "brl" spellings become BRL. See CategoryNormalizationSql.
            foreach (var statement in CategoryNormalizationSql.UpStatements)
            {
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible on purpose: the original spellings and the merged allocations are not kept.
        }
    }
}
