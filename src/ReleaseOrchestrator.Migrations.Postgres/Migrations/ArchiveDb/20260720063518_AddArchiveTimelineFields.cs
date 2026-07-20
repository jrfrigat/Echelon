using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class AddArchiveTimelineFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "ArchivedTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstSeenSource",
                table: "ArchivedTasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ArchivedMergeRequests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "ArchivedTasks");

            migrationBuilder.DropColumn(
                name: "FirstSeenSource",
                table: "ArchivedTasks");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ArchivedMergeRequests");
        }
    }
}
