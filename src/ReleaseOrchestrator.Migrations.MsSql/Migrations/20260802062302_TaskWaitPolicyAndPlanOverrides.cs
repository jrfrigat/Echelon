using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class TaskWaitPolicyAndPlanOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlanOverrides_RolloutPlans_RolloutPlanId",
                table: "PlanOverrides");

            migrationBuilder.DropIndex(
                name: "IX_PlanOverrides_RolloutPlanId",
                table: "PlanOverrides");

            migrationBuilder.RenameColumn(
                name: "RolloutPlanId",
                table: "PlanOverrides",
                newName: "TaskId");

            migrationBuilder.AddColumn<int>(
                name: "PrerequisiteGroupOrder",
                table: "Tasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitForLinked",
                table: "Tasks",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitForSubtasks",
                table: "Tasks",
                type: "bit",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanningSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WaitForSubtasks = table.Column<bool>(type: "bit", nullable: false),
                    WaitForLinked = table.Column<bool>(type: "bit", nullable: false),
                    PrerequisiteGroupOrder = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanningSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskPrerequisiteOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskPrerequisiteOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskPrerequisiteOrders_Tasks_PrerequisiteTaskId",
                        column: x => x.PrerequisiteTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskPrerequisiteOrders_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanOverride_Task_Kind",
                table: "PlanOverrides",
                columns: new[] { "TaskId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskPrerequisiteOrder_Task_Position",
                table: "TaskPrerequisiteOrders",
                columns: new[] { "TaskId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskPrerequisiteOrder_Task_Prerequisite",
                table: "TaskPrerequisiteOrders",
                columns: new[] { "TaskId", "PrerequisiteTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskPrerequisiteOrders_PrerequisiteTaskId",
                table: "TaskPrerequisiteOrders",
                column: "PrerequisiteTaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlanOverrides_Tasks_TaskId",
                table: "PlanOverrides",
                column: "TaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlanOverrides_Tasks_TaskId",
                table: "PlanOverrides");

            migrationBuilder.DropTable(
                name: "PlanningSettings");

            migrationBuilder.DropTable(
                name: "TaskPrerequisiteOrders");

            migrationBuilder.DropIndex(
                name: "IX_PlanOverride_Task_Kind",
                table: "PlanOverrides");

            migrationBuilder.DropColumn(
                name: "PrerequisiteGroupOrder",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WaitForLinked",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WaitForSubtasks",
                table: "Tasks");

            migrationBuilder.RenameColumn(
                name: "TaskId",
                table: "PlanOverrides",
                newName: "RolloutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanOverrides_RolloutPlanId",
                table: "PlanOverrides",
                column: "RolloutPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlanOverrides_RolloutPlans_RolloutPlanId",
                table: "PlanOverrides",
                column: "RolloutPlanId",
                principalTable: "RolloutPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
