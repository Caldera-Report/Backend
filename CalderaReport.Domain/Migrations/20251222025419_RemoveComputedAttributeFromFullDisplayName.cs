using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveComputedAttributeFromFullDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
            ALTER TABLE "Players"
            ALTER COLUMN "FullDisplayName" DROP EXPRESSION;
        """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
            ALTER TABLE "Players"
            ALTER COLUMN "FullDisplayName"
            ADD GENERATED ALWAYS AS ("DisplayName" || '#' || lpad("DisplayNameCode"::text, 4, '0')) STORED;
        """);
        }
    }
}
