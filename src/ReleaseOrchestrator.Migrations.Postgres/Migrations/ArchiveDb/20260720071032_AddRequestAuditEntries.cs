using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class AddRequestAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Host = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Instance = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RoutePattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PeerIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ForwardedIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Permission = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsNotable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestAuditEntries_Notable_StartedAt",
                table: "RequestAuditEntries",
                columns: new[] { "IsNotable", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestAuditEntries_StartedAt",
                table: "RequestAuditEntries",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestAuditEntries_UserId_StartedAt",
                table: "RequestAuditEntries",
                columns: new[] { "UserId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestAuditEntries");
        }
    }
}
