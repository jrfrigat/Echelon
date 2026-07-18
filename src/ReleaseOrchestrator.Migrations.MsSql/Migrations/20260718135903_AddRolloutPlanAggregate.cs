using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRolloutPlanAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentEnvironments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RolloutPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    YamlHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConflictsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolloutPlans_Tasks_TargetTaskId",
                        column: x => x.TargetTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RolloutPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanOverrides_RolloutPlans_RolloutPlanId",
                        column: x => x.RolloutPlanId,
                        principalTable: "RolloutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanTaskNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RolloutPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanTaskNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanTaskNodes_RolloutPlans_RolloutPlanId",
                        column: x => x.RolloutPlanId,
                        principalTable: "RolloutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanTaskNodes_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanTaskNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeployStrategyKeyOverride = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IntraTaskOrder = table.Column<int>(type: "int", nullable: true),
                    ManualInclusion = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanItems_MergeRequests_MergeRequestId",
                        column: x => x.MergeRequestId,
                        principalTable: "MergeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanItems_PlanTaskNodes_PlanTaskNodeId",
                        column: x => x.PlanTaskNodeId,
                        principalTable: "PlanTaskNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentEnvironment_Key",
                table: "DeploymentEnvironments",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanItem_MergeRequestId",
                table: "PlanItems",
                column: "MergeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanItem_Node_Mr",
                table: "PlanItems",
                columns: new[] { "PlanTaskNodeId", "MergeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanOverrides_RolloutPlanId",
                table: "PlanOverrides",
                column: "RolloutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanTaskNode_Plan_Task",
                table: "PlanTaskNodes",
                columns: new[] { "RolloutPlanId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanTaskNodes_TaskId",
                table: "PlanTaskNodes",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutPlan_CreatedAt",
                table: "RolloutPlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutPlan_TargetTaskId_Active",
                table: "RolloutPlans",
                column: "TargetTaskId",
                unique: true,
                filter: "[IsActive] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentEnvironments");

            migrationBuilder.DropTable(
                name: "PlanItems");

            migrationBuilder.DropTable(
                name: "PlanOverrides");

            migrationBuilder.DropTable(
                name: "PlanTaskNodes");

            migrationBuilder.DropTable(
                name: "RolloutPlans");
        }
    }
}
