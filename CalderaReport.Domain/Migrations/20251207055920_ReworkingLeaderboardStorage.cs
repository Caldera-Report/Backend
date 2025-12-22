using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ReworkingLeaderboardStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayerLeaderboards_ActivityId_LeaderboardType_Rank",
                table: "PlayerLeaderboards");

            migrationBuilder.DropIndex(
                name: "idx_playerleaderboards_fulldisplayname_trgm",
                table: "PlayerLeaderboards");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers");

            migrationBuilder.DropColumn(
                name: "FullDisplayName",
                table: "PlayerLeaderboards");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "PlayerLeaderboards");

            migrationBuilder.Sql("TRUNCATE \"PlayerLeaderboards\";");

            migrationBuilder.DropColumn(
                name: "Data",
                table: "PlayerLeaderboards");

            migrationBuilder.AddColumn<long>(
                name: "Data",
                table: "PlayerLeaderboards",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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

            migrationBuilder.CreateIndex(
                name: "IX_PlayerLeaderboards_ActivityId_LeaderboardType_Data",
                table: "PlayerLeaderboards",
                columns: new[] { "ActivityId", "LeaderboardType", "Data" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayerLeaderboards_ActivityId_LeaderboardType_Data",
                table: "PlayerLeaderboards");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "ActivityReportPlayers");

            migrationBuilder.DropColumn(
                name: "Data",
                table: "PlayerLeaderboards");

            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "PlayerLeaderboards",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FullDisplayName",
                table: "PlayerLeaderboards",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "PlayerLeaderboards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ActivityReportPlayers",
                table: "ActivityReportPlayers",
                columns: new[] { "ActivityReportId", "PlayerId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerLeaderboards_ActivityId_LeaderboardType_Rank",
                table: "PlayerLeaderboards",
                columns: new[] { "ActivityId", "LeaderboardType", "Rank" });
        }
    }
}
