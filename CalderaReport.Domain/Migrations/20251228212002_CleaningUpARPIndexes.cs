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
            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId_Completed_Duration",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId_PlayerId",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId_Score",
                table: "ActivityReportPlayers");

            migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY
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
