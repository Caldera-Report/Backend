using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CleaningUpARPIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_ActivityReportPlayers_ActivityId_Completed_Duration"";
                DROP INDEX IF EXISTS ""IX_ActivityReportPlayers_ActivityId_PlayerId"";
                DROP INDEX IF EXISTS ""IX_ActivityReportPlayers_ActivityId_Score"";
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS ""IX_ActivityReportPlayers_PlayerId_Completed""
                ON ""ActivityReportPlayers"" (""PlayerId"", ""Completed"")
                INCLUDE (""ActivityId"", ""ActivityReportId"");
                ", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityId_Completed_Duration",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityId", "Completed", "Duration" },
                filter: "\"Completed\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityId_PlayerId",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityId", "PlayerId" },
                filter: "\"Completed\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityId_Score",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityId", "Score" },
                filter: "\"Completed\" = TRUE");
        }
    }
}
