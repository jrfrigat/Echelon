using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class SplitYandexTrackerIngestionTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only, no schema change: the tracker is now two provider types. An existing connection
            // was reached by webhook, so it becomes the webhook type; nothing else about it changes.
            migrationBuilder.Sql(
                "UPDATE [TrackerConnections] SET [ProviderType] = 'yandextracker-webhook' WHERE [ProviderType] = 'yandextracker';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fold both split types back to the single legacy key.
            migrationBuilder.Sql(
                "UPDATE [TrackerConnections] SET [ProviderType] = 'yandextracker' WHERE [ProviderType] IN ('yandextracker-webhook', 'yandextracker-poll');");
        }
    }
}
