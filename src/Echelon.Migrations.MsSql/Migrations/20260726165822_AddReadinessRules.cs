using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddReadinessRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Readiness moved from inline columns (a mode + a label set) to named ReadinessRules an
            // environment points at. There is no faithful automatic mapping from the old label set to a
            // rule, so rather than silently ungate an environment that had a gate, disable it: it
            // cannot be launched to until an operator assigns a readiness rule. Runs before the column
            // is dropped, and fails closed -- a gated environment never becomes an ungated one.
            migrationBuilder.Sql(
                "UPDATE [DeploymentEnvironments] SET [IsEnabled] = 0 WHERE [ReadyRule] <> 'NoGate';");

            migrationBuilder.DropColumn(
                name: "ReadyLabels",
                table: "DeploymentEnvironments");

            migrationBuilder.DropColumn(
                name: "ReadyRule",
                table: "DeploymentEnvironments");

            migrationBuilder.AddColumn<Guid>(
                name: "ReadinessRuleId",
                table: "RepositoryDeployTargets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReadinessRuleId",
                table: "DeploymentEnvironments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReadinessRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiredSignals = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadinessRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryDeployTargets_ReadinessRuleId",
                table: "RepositoryDeployTargets",
                column: "ReadinessRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentEnvironments_ReadinessRuleId",
                table: "DeploymentEnvironments",
                column: "ReadinessRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadinessRule_Name",
                table: "ReadinessRules",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DeploymentEnvironments_ReadinessRules_ReadinessRuleId",
                table: "DeploymentEnvironments",
                column: "ReadinessRuleId",
                principalTable: "ReadinessRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryDeployTargets_ReadinessRules_ReadinessRuleId",
                table: "RepositoryDeployTargets",
                column: "ReadinessRuleId",
                principalTable: "ReadinessRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DeploymentEnvironments_ReadinessRules_ReadinessRuleId",
                table: "DeploymentEnvironments");

            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryDeployTargets_ReadinessRules_ReadinessRuleId",
                table: "RepositoryDeployTargets");

            migrationBuilder.DropTable(
                name: "ReadinessRules");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryDeployTargets_ReadinessRuleId",
                table: "RepositoryDeployTargets");

            migrationBuilder.DropIndex(
                name: "IX_DeploymentEnvironments_ReadinessRuleId",
                table: "DeploymentEnvironments");

            migrationBuilder.DropColumn(
                name: "ReadinessRuleId",
                table: "RepositoryDeployTargets");

            migrationBuilder.DropColumn(
                name: "ReadinessRuleId",
                table: "DeploymentEnvironments");

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
                // NoGate, not "": the enum has no zero member, so an empty value fails to parse on read.
                defaultValue: "NoGate");
        }
    }
}
