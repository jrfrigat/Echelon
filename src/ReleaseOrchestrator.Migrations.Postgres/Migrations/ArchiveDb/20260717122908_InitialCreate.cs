using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivedMergeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RepositoryName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceBranch = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TargetBranch = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TaskExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MergedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedMergeRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchivedReleasePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedReleasePlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchivedTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DependenciesJson = table.Column<string>(type: "text", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedMergeRequests_ClosedAt",
                table: "ArchivedMergeRequests",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedMergeRequests_MergedAt",
                table: "ArchivedMergeRequests",
                column: "MergedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedMergeRequests_TaskExternalId",
                table: "ArchivedMergeRequests",
                column: "TaskExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedReleasePlans_CreatedAt",
                table: "ArchivedReleasePlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedTasks_ClosedAt",
                table: "ArchivedTasks",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedTasks_ExternalId",
                table: "ArchivedTasks",
                column: "ExternalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivedMergeRequests");

            migrationBuilder.DropTable(
                name: "ArchivedReleasePlans");

            migrationBuilder.DropTable(
                name: "ArchivedTasks");
        }
    }
}
