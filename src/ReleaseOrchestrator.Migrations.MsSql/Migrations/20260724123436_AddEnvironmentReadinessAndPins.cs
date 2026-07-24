using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentReadinessAndPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReadyLabels",
                table: "DeploymentEnvironments",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReadyRule",
                table: "DeploymentEnvironments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NoGate");

            migrationBuilder.CreateTable(
                name: "MergeRequestReadinessPins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsReady = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorOid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeRequestReadinessPins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MergeRequestReadinessPins_DeploymentEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DeploymentEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MergeRequestReadinessPins_MergeRequests_MergeRequestId",
                        column: x => x.MergeRequestId,
                        principalTable: "MergeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestReadinessPin_Mr_Environment",
                table: "MergeRequestReadinessPins",
                columns: new[] { "MergeRequestId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestReadinessPins_EnvironmentId",
                table: "MergeRequestReadinessPins",
                column: "EnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MergeRequestReadinessPins");

            migrationBuilder.DropColumn(
                name: "ReadyLabels",
                table: "DeploymentEnvironments");

            migrationBuilder.DropColumn(
                name: "ReadyRule",
                table: "DeploymentEnvironments");
        }
    }
}
