using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SwappingCharacterIdForSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "ActivityReportPlayers");

            migrationBuilder.AddColumn<int>(
                name: "SessionId",
                table: "ActivityReportPlayers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "PlayerId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "ActivityReportPlayers");

            migrationBuilder.AddColumn<long>(
                name: "CharacterId",
                table: "ActivityReportPlayers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "CharacterId", "PlayerId" });
        }
    }
}
