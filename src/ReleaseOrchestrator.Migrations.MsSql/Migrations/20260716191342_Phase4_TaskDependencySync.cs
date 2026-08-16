using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class Phase4_TaskDependencySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Repositories_ConnectionId",
                table: "Repositories");

            migrationBuilder.AddColumn<Guid>(
                name: "TrackerConnectionId",
                table: "Repositories",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskExternalId",
                table: "MergeRequests",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Backfill the key from the task each MR is already linked to, so merge requests
            // imported before this column existed stay linkable. Without it they look like MRs
            // whose branch named no task, and a re-linking pass would skip them forever.
            migrationBuilder.Sql("""
                UPDATE mr
                SET mr.[TaskExternalId] = t.[ExternalId]
                FROM [MergeRequests] mr
                JOIN [Tasks] t ON t.[Id] = mr.[TaskId]
                WHERE mr.[TaskId] IS NOT NULL;
                """);

            // Repositories become unique per (connection, external id) below. Webhook routing
            // already resolved them with FirstOrDefault, so a duplicate silently diverted merge
            // requests to whichever row came back first - collapse them onto the oldest.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [ConnectionId], [ExternalId] ORDER BY [Id]) AS rn,
                           FIRST_VALUE([Id]) OVER (PARTITION BY [ConnectionId], [ExternalId] ORDER BY [Id]) AS keep_id
                    FROM [Repositories]
                )
                UPDATE target SET target.[RepositoryId] = r.keep_id
                FROM [MergeRequests] target
                JOIN ranked r ON r.[Id] = target.[RepositoryId]
                WHERE r.rn > 1;
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [RepositoryId], [StackId],
                           ROW_NUMBER() OVER (PARTITION BY [StackId], [RepositoryId] ORDER BY [RepositoryId]) AS rn
                    FROM [RepositoryStacks]
                )
                DELETE rs
                FROM [RepositoryStacks] rs
                JOIN [Repositories] dup ON dup.[Id] = rs.[RepositoryId]
                JOIN (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [ConnectionId], [ExternalId] ORDER BY [Id]) AS rn
                    FROM [Repositories]
                ) r ON r.[Id] = dup.[Id]
                WHERE r.rn > 1;
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT [Id],
                           ROW_NUMBER() OVER (PARTITION BY [ConnectionId], [ExternalId] ORDER BY [Id]) AS rn
                    FROM [Repositories]
                )
                DELETE FROM [Repositories]
                WHERE [Id] IN (SELECT [Id] FROM ranked WHERE rn > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_TrackerConnectionId",
                table: "Repositories",
                column: "TrackerConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Repository_ConnectionId_ExternalId",
                table: "Repositories",
                columns: new[] { "ConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MergeRequest_TaskExternalId",
                table: "MergeRequests",
                column: "TaskExternalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Repositories_TrackerConnections_TrackerConnectionId",
                table: "Repositories",
                column: "TrackerConnectionId",
                principalTable: "TrackerConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Repositories_TrackerConnections_TrackerConnectionId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_Repositories_TrackerConnectionId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_Repository_ConnectionId_ExternalId",
                table: "Repositories");

            migrationBuilder.DropIndex(
                name: "IX_MergeRequest_TaskExternalId",
                table: "MergeRequests");

            migrationBuilder.DropColumn(
                name: "TrackerConnectionId",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "TaskExternalId",
                table: "MergeRequests");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ConnectionId",
                table: "Repositories",
                column: "ConnectionId");
        }
    }
}
