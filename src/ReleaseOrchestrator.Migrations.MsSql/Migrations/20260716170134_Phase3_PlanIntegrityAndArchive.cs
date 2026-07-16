using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class Phase3_PlanIntegrityAndArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskItem_TrackerConnectionId_ExternalId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_TaskDependencies_DependentTaskId",
                table: "TaskDependencies");

            migrationBuilder.DropIndex(
                name: "IX_ReleasePlan_IsActive",
                table: "ReleasePlans");

            migrationBuilder.AddColumn<string>(
                name: "ReadyForDeployLabel",
                table: "VcsConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Tasks",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictsJson",
                table: "ReleasePlans",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnapshotStartedAt",
                table: "ReleasePlans",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "MergeRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsStatusManual",
                table: "MergeRequests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "MergeRequests",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            // Data fixes must land before the indexes below, which will not build against the
            // rows as they stand.

            // A plan built before this column existed was computed from data read at roughly
            // its creation time. Leaving the 0001-01-01 default would make every existing plan
            // look older than any recalculation request and force a pointless rebuild.
            migrationBuilder.Sql("UPDATE [ReleasePlans] SET [SnapshotStartedAt] = [CreatedAt];");

            // At most one plan may be active from here on. Existing rows can hold several — an
            // imported plan beside an auto-generated one, which is exactly the corruption the
            // filtered unique index exists to prevent. Keep the newest, stand the rest down.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id], ROW_NUMBER() OVER (ORDER BY [CreatedAt] DESC, [Id] DESC) AS rn
                    FROM [ReleasePlans]
                    WHERE [IsActive] = 1
                )
                UPDATE [ReleasePlans] SET [IsActive] = 0
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            // Duplicate merge requests could accumulate before the natural key was unique
            // (check-then-insert under at-least-once delivery). Collapse them onto the oldest
            // row, moving any plan items across, or the unique index will not build.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id], [RepositoryId], [ExternalId],
                           ROW_NUMBER() OVER (PARTITION BY [RepositoryId], [ExternalId] ORDER BY [CreatedAt], [Id]) AS rn,
                           FIRST_VALUE([Id]) OVER (PARTITION BY [RepositoryId], [ExternalId] ORDER BY [CreatedAt], [Id]) AS keep_id
                    FROM [MergeRequests]
                )
                UPDATE si SET si.[MergeRequestId] = r.keep_id
                FROM [StageItems] si
                JOIN ranked r ON r.[Id] = si.[MergeRequestId]
                WHERE r.rn > 1;
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [RepositoryId], [ExternalId] ORDER BY [CreatedAt], [Id]) AS rn
                    FROM [MergeRequests]
                )
                DELETE FROM [MergeRequests]
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            // Same story for tasks, whose natural key is now unique too.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [TrackerConnectionId], [ExternalId] ORDER BY [Id]) AS rn,
                           FIRST_VALUE([Id]) OVER (PARTITION BY [TrackerConnectionId], [ExternalId] ORDER BY [Id]) AS keep_id
                    FROM [Tasks]
                )
                UPDATE mr SET mr.[TaskId] = r.keep_id
                FROM [MergeRequests] mr
                JOIN ranked r ON r.[Id] = mr.[TaskId]
                WHERE r.rn > 1;
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [TrackerConnectionId], [ExternalId] ORDER BY [Id]) AS rn
                    FROM [Tasks]
                )
                DELETE FROM [TaskDependencies]
                WHERE [DependentTaskId] IN (SELECT [Id] FROM ranked WHERE rn > 1)
                   OR [DependsOnTaskId] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [TrackerConnectionId], [ExternalId] ORDER BY [Id]) AS rn
                    FROM [Tasks]
                )
                DELETE FROM [Tasks]
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            // Dependency edges are about to become unique per (dependent, prerequisite).
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [DependentTaskId], [DependsOnTaskId] ORDER BY [Id]) AS rn
                    FROM [TaskDependencies]
                )
                DELETE FROM [TaskDependencies]
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_ClosedAt",
                table: "Tasks",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TrackerConnectionId_ExternalId",
                table: "Tasks",
                columns: new[] { "TrackerConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependency_Dependent_DependsOn",
                table: "TaskDependencies",
                columns: new[] { "DependentTaskId", "DependsOnTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleasePlan_CreatedAt",
                table: "ReleasePlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReleasePlan_IsActive",
                table: "ReleasePlans",
                column: "IsActive",
                unique: true,
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequest_ClosedAt",
                table: "MergeRequests",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequest_MergedAt",
                table: "MergeRequests",
                column: "MergedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequest_RepositoryId_ExternalId",
                table: "MergeRequests",
                columns: new[] { "RepositoryId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequest_Status",
                table: "MergeRequests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropIndex(
                name: "IX_TaskItem_ClosedAt",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_TaskItem_TrackerConnectionId_ExternalId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_TaskDependency_Dependent_DependsOn",
                table: "TaskDependencies");

            migrationBuilder.DropIndex(
                name: "IX_ReleasePlan_CreatedAt",
                table: "ReleasePlans");

            migrationBuilder.DropIndex(
                name: "IX_ReleasePlan_IsActive",
                table: "ReleasePlans");

            migrationBuilder.DropIndex(
                name: "IX_MergeRequest_ClosedAt",
                table: "MergeRequests");

            migrationBuilder.DropIndex(
                name: "IX_MergeRequest_MergedAt",
                table: "MergeRequests");

            migrationBuilder.DropIndex(
                name: "IX_MergeRequest_RepositoryId_ExternalId",
                table: "MergeRequests");

            migrationBuilder.DropIndex(
                name: "IX_MergeRequest_Status",
                table: "MergeRequests");

            migrationBuilder.DropColumn(
                name: "ReadyForDeployLabel",
                table: "VcsConnections");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ConflictsJson",
                table: "ReleasePlans");

            migrationBuilder.DropColumn(
                name: "SnapshotStartedAt",
                table: "ReleasePlans");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "MergeRequests");

            migrationBuilder.DropColumn(
                name: "IsStatusManual",
                table: "MergeRequests");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "MergeRequests");

            migrationBuilder.CreateIndex(
                name: "IX_TaskItem_TrackerConnectionId_ExternalId",
                table: "Tasks",
                columns: new[] { "TrackerConnectionId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_DependentTaskId",
                table: "TaskDependencies",
                column: "DependentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ReleasePlan_IsActive",
                table: "ReleasePlans",
                column: "IsActive");
        }
    }
}
