using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations.ArchiveDb
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Instance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RoutePattern = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PeerIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ForwardedIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Permission = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsNotable = table.Column<bool>(type: "bit", nullable: false)
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
