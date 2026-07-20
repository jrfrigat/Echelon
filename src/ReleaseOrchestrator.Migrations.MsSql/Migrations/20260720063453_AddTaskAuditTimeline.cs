using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskAuditTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSeenAt",
                table: "Tasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstSeenSource",
                table: "Tasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LaunchedByKind",
                table: "Rollouts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LaunchedByName",
                table: "Rollouts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "RolloutPlans",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByKind",
                table: "RolloutPlans",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "RolloutPlans",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByOid",
                table: "RolloutPlans",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorKind",
                table: "RolloutEvents",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorName",
                table: "RolloutEvents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorOid",
                table: "RolloutEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MergeRequestStatusChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergeRequestExternalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FromStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorOid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeRequestStatusChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolloutPlan_TargetTaskId_CreatedAt",
                table: "RolloutPlans",
                columns: new[] { "TargetTaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestStatusChange_MergeRequestId_At",
                table: "MergeRequestStatusChanges",
                columns: new[] { "MergeRequestId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequestStatusChange_TaskId_At",
                table: "MergeRequestStatusChanges",
                columns: new[] { "TaskId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MergeRequestStatusChanges");

            migrationBuilder.DropIndex(
                name: "IX_RolloutPlan_TargetTaskId_CreatedAt",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "FirstSeenAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "FirstSeenSource",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "LaunchedByKind",
                table: "Rollouts");

            migrationBuilder.DropColumn(
                name: "LaunchedByName",
                table: "Rollouts");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "CreatedByKind",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "CreatedByOid",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "ActorKind",
                table: "RolloutEvents");

            migrationBuilder.DropColumn(
                name: "ActorName",
                table: "RolloutEvents");

            migrationBuilder.DropColumn(
                name: "ActorOid",
                table: "RolloutEvents");
        }
    }
}
