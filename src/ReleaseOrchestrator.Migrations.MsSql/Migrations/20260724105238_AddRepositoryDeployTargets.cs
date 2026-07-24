using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryDeployTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeploySettingsJson",
                table: "RolloutSteps",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RepositoryDeployTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeployStrategyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeploySettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedeployPolicy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryDeployTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryDeployTargets_DeploymentEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DeploymentEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepositoryDeployTargets_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryDeployTarget_Repository_Environment",
                table: "RepositoryDeployTargets",
                columns: new[] { "RepositoryId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryDeployTargets_EnvironmentId",
                table: "RepositoryDeployTargets",
                column: "EnvironmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryDeployTargets");

            migrationBuilder.DropColumn(
                name: "DeploySettingsJson",
                table: "RolloutSteps");
        }
    }
}
