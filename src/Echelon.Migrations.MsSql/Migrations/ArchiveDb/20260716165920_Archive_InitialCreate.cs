using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class Archive_InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivedMergeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RepositoryName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourceBranch = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TargetBranch = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TaskExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MergedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedMergeRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchivedReleasePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PlanJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedReleasePlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArchivedTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DependenciesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ArchivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
