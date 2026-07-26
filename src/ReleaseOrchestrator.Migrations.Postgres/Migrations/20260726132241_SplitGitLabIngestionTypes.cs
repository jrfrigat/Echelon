using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SplitGitLabIngestionTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data first, while the columns still exist: push vs poll is now the provider type, and a
            // poll connection's interval moves into the settings bag. IngestionMode is the enum's int
            // (Push = 1, Poll = 2). GitLab declared no other settings, so overwriting the (null) bag
            // for a poll connection is safe.
            migrationBuilder.Sql("""
                UPDATE "VcsConnections"
                SET "ProviderSettingsJson" = '{"pollIntervalSeconds":"' || "PollIntervalSeconds"::text || '"}'
                WHERE "ProviderType" = 'gitlab' AND "IngestionMode" = 2;
                """);
            migrationBuilder.Sql(
                "UPDATE \"VcsConnections\" SET \"ProviderType\" = 'gitlab-poll' WHERE \"ProviderType\" = 'gitlab' AND \"IngestionMode\" = 2;");
            migrationBuilder.Sql(
                "UPDATE \"VcsConnections\" SET \"ProviderType\" = 'gitlab-webhook' WHERE \"ProviderType\" = 'gitlab';");

            migrationBuilder.DropColumn(
                name: "IngestionMode",
                table: "VcsConnections");

            migrationBuilder.DropColumn(
                name: "PollIntervalSeconds",
                table: "VcsConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add with the model's original defaults (Push, 300s), then reverse the mapping while
            // the split types still exist: mark poll rows, recover their interval from the bag, clear
            // the bag, and fold both types back to 'gitlab'.
            migrationBuilder.AddColumn<int>(
                name: "IngestionMode",
                table: "VcsConnections",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalSeconds",
                table: "VcsConnections",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.Sql(
                "UPDATE \"VcsConnections\" SET \"IngestionMode\" = 2 WHERE \"ProviderType\" = 'gitlab-poll';");
            // json (not jsonb) so a malformed bag does not fail the whole migration; ->> yields text,
            // cast to int only when it is all digits.
            migrationBuilder.Sql("""
                UPDATE "VcsConnections"
                SET "PollIntervalSeconds" = ("ProviderSettingsJson"::json->>'pollIntervalSeconds')::int
                WHERE "ProviderType" = 'gitlab-poll'
                  AND "ProviderSettingsJson" IS NOT NULL
                  AND ("ProviderSettingsJson"::json->>'pollIntervalSeconds') ~ '^[0-9]+$';
                """);
            migrationBuilder.Sql(
                "UPDATE \"VcsConnections\" SET \"ProviderSettingsJson\" = NULL WHERE \"ProviderType\" IN ('gitlab-poll', 'gitlab-webhook');");
            migrationBuilder.Sql(
                "UPDATE \"VcsConnections\" SET \"ProviderType\" = 'gitlab' WHERE \"ProviderType\" IN ('gitlab-poll', 'gitlab-webhook');");
        }
    }
}
