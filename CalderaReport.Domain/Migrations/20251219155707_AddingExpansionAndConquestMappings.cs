using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace API.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddingExpansionAndConquestMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Expansions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ReleaseDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expansions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConquestMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BungieActivityId = table.Column<long>(type: "bigint", nullable: false),
                    ActivityId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ExpansionId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConquestMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConquestMappings_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConquestMappings_Expansions_ExpansionId",
                        column: x => x.ExpansionId,
                        principalTable: "Expansions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConquestMappings_ActivityId",
                table: "ConquestMappings",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ConquestMappings_ExpansionId",
                table: "ConquestMappings",
                column: "ExpansionId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlayerCrawlQueue_Players_PlayerId",
                table: "PlayerCrawlQueue",
                column: "PlayerId",
                principalTable: "Players",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.idx_activityreports_needsfullcheck_false;");

            migrationBuilder.Sql("DROP INDEX IF EXISTS public.idx_activityreports_needsfullcheck_false_id;");

            /*migrationBuilder.Sql(@"
                CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_activityreports_needsfullcheck_true_id
                    ON ""ActivityReports"" (""Id"")
                    WHERE ""NeedsFullCheck"" IS TRUE;
            ");
            Had to add it manually due to issues with concurrent index creation in migrations
            */
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlayerCrawlQueue_Players_PlayerId",
                table: "PlayerCrawlQueue");

            migrationBuilder.DropTable(
                name: "ConquestMappings");

            migrationBuilder.DropTable(
                name: "Expansions");

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS public.idx_activityreports_needsfullcheck_true_id;");
        }
    }
}
