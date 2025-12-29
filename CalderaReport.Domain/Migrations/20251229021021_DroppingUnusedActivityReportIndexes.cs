using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class DroppingUnusedActivityReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_ActivityId_Date",
                table: "ActivityReports");

            migrationBuilder.DropIndex(
                name: "IX_ActivityReports_ActivityId",
                table: "ActivityReports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_ActivityId_Date",
                table: "ActivityReports",
                columns: new[] { "ActivityId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityReports_ActivityId",
                table: "ActivityReports",
                column: "ActivityId");
        }
    }
}
