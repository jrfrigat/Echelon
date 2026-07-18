using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class RetireArchivedReleasePlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivedReleasePlans");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivedReleasePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedReleasePlans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedReleasePlans_CreatedAt",
                table: "ArchivedReleasePlans",
                column: "CreatedAt");
        }
    }
}
