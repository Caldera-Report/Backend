using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ReAddLastCrawled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCrawlCompleted",
                table: "Players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCrawlStarted",
                table: "Players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerCrawlQueue_PlayerId",
                table: "PlayerCrawlQueue",
                column: "PlayerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayerCrawlQueue_PlayerId",
                table: "PlayerCrawlQueue");

            migrationBuilder.DropColumn(
                name: "LastCrawlCompleted",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "LastCrawlStarted",
                table: "Players");
        }
    }
}
