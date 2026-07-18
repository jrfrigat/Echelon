using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MrDeployClaims",
                columns: table => new
                {
                    MergeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    OwnerRolloutId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrDeployClaims", x => new { x.MergeRequestId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_MrDeployClaims_DeploymentEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DeploymentEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MrDeployClaims_MergeRequests_MergeRequestId",
                        column: x => x.MergeRequestId,
                        principalTable: "MergeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MrDeploymentStates",
                columns: table => new
                {
                    MergeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrDeploymentStates", x => new { x.MergeRequestId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_MrDeploymentStates_DeploymentEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DeploymentEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MrDeploymentStates_MergeRequests_MergeRequestId",
                        column: x => x.MergeRequestId,
                        principalTable: "MergeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Rollouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LaunchedByOid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rollouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rollouts_DeploymentEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DeploymentEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rollouts_RolloutPlans_RolloutPlanId",
                        column: x => x.RolloutPlanId,
                        principalTable: "RolloutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Rollouts_Tasks_TargetTaskId",
                        column: x => x.TargetTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolloutEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolloutEvents_Rollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalTable: "Rollouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolloutSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutId = table.Column<Guid>(type: "uuid", nullable: false),
                    MergeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Wave = table.Column<int>(type: "integer", nullable: false),
                    DeployStrategyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ExternalRef = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MergeShaAtSnapshot = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolloutSteps_MergeRequests_MergeRequestId",
                        column: x => x.MergeRequestId,
                        principalTable: "MergeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolloutSteps_Rollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalTable: "Rollouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolloutSteps_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RolloutStepAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNo = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutStepAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RolloutStepAttempts_RolloutSteps_RolloutStepId",
                        column: x => x.RolloutStepId,
                        principalTable: "RolloutSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MrDeployClaims_EnvironmentId",
                table: "MrDeployClaims",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MrDeploymentStates_EnvironmentId",
                table: "MrDeploymentStates",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutEvents_RolloutId",
                table: "RolloutEvents",
                column: "RolloutId");

            migrationBuilder.CreateIndex(
                name: "IX_Rollout_IdempotencyKey",
                table: "Rollouts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rollout_TargetTaskId",
                table: "Rollouts",
                column: "TargetTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Rollouts_EnvironmentId",
                table: "Rollouts",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Rollouts_RolloutPlanId",
                table: "Rollouts",
                column: "RolloutPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutStepAttempts_RolloutStepId",
                table: "RolloutStepAttempts",
                column: "RolloutStepId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutStep_Rollout_Mr",
                table: "RolloutSteps",
                columns: new[] { "RolloutId", "MergeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RolloutSteps_MergeRequestId",
                table: "RolloutSteps",
                column: "MergeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutSteps_TaskId",
                table: "RolloutSteps",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MrDeployClaims");

            migrationBuilder.DropTable(
                name: "MrDeploymentStates");

            migrationBuilder.DropTable(
                name: "RolloutEvents");

            migrationBuilder.DropTable(
                name: "RolloutStepAttempts");

            migrationBuilder.DropTable(
                name: "RolloutSteps");

            migrationBuilder.DropTable(
                name: "Rollouts");
        }
    }
}
