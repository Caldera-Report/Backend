using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_Id",
                table: "ActivityReports");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_Id_ActivityId",
                table: "ActivityReports");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_Id_ActivityId_Date",
                table: "ActivityReports");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId_Duration",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId_Score",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "idx_arp_activityid_cover",
                table: "ActivityReportPlayers");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_ActivityId_Date",
                table: "ActivityReports",
                columns: new[] { "ActivityId", "Date" });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_ActivityId_Date",
                table: "ActivityReports");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId_PlayerId",
                table: "ActivityReportPlayers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReportPlayers_ActivityId_Score",
                table: "ActivityReportPlayers");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_Id",
                table: "ActivityReports",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_Id_ActivityId",
                table: "ActivityReports",
                columns: new[] { "Id", "ActivityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_Id_ActivityId_Date",
                table: "ActivityReports",
                columns: new[] { "Id", "ActivityId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityId",
                table: "ActivityReportPlayers",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "PlayerId" },
                filter: "\"Completed\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId_Duration",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "PlayerId", "Duration" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReportPlayers_ActivityReportId_PlayerId_Score",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "PlayerId", "Score" });
        }
    }
}
