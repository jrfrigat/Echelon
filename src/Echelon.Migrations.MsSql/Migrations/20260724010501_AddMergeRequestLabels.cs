using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddMergeRequestLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Labels",
                table: "MergeRequests",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "MergeRequestLabelChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergeRequestExternalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FromLabels = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ToLabels = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorOid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeRequestLabelChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestLabelChange_MergeRequestId_At",
                table: "MergeRequestLabelChanges",
                columns: new[] { "MergeRequestId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestLabelChange_TaskId_At",
                table: "MergeRequestLabelChanges",
                columns: new[] { "TaskId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MergeRequestLabelChanges");

            migrationBuilder.DropColumn(
                name: "Labels",
                table: "MergeRequests");
        }
    }
}
