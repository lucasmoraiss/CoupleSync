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
            // The first statements copy the original values into _backup_20261006_* tables (outside the EF model).
            foreach (var statement in CategoryNormalizationSql.UpStatements)
            {
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversed on purpose: the data stays normalized. The original values live in the _backup_20261006_* tables,
            // which are left alone here.
        }
    }
}
