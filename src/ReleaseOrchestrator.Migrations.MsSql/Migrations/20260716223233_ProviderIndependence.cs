using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <summary>
    /// Turns the provider-type enums into adapter keys, and the Yandex-specific OrgId column into
    /// an opaque per-provider settings bag.
    /// </summary>
    /// <remarks>
    /// Hand-edited. The scaffolded version dropped VcsType, TrackerType and OrgId and added the
    /// new columns with an empty default - data loss, not a rename: every existing connection
    /// would have come back with ProviderType = '', resolved to no adapter, and lost its
    /// organization id on the way. Each column is therefore added, backfilled, and only then is
    /// the old one dropped.
    ///
    /// Rows holding an enum value this migration does not know are left with an empty
    /// ProviderType on purpose. There is nothing to map them to, and an empty value fails loudly
    /// at the provider factory - naming the connection and listing the registered providers -
    /// which beats guessing at 'gitlab' and quietly pointing a connection at the wrong API.
    /// </remarks>
    public partial class ProviderIndependence : Migration
    {
        // The enum members these columns held: VcsType.GitLab = 1, TrackerType.YandexTracker = 1.
        private const int LegacyGitLab = 1;
        private const int LegacyYandexTracker = 1;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "VcsConnections",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderType",
                table: "TrackerConnections",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderSettingsJson",
                table: "TrackerConnections",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.Sql($"""
                UPDATE [VcsConnections]
                SET [ProviderType] = CASE [VcsType] WHEN {LegacyGitLab} THEN 'gitlab' ELSE '' END;
                """);

            migrationBuilder.Sql($"""
                UPDATE [TrackerConnections]
                SET [ProviderType] = CASE [TrackerType] WHEN {LegacyYandexTracker} THEN 'yandextracker' ELSE '' END;
                """);

            // STRING_ESCAPE rather than plain concatenation: an organization id is operator input,
            // and a quote or backslash in it would otherwise produce a settings bag that is not
            // valid JSON - which the factory rejects, taking the whole connection down with it.
            migrationBuilder.Sql("""
                UPDATE [TrackerConnections]
                SET [ProviderSettingsJson] = '{"orgId":"' + STRING_ESCAPE([OrgId], 'json') + '"}'
                WHERE [OrgId] IS NOT NULL AND LTRIM(RTRIM([OrgId])) <> '';
                """);

            migrationBuilder.DropColumn(name: "VcsType", table: "VcsConnections");
            migrationBuilder.DropColumn(name: "TrackerType", table: "TrackerConnections");
            migrationBuilder.DropColumn(name: "OrgId", table: "TrackerConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VcsType",
                table: "VcsConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TrackerType",
                table: "TrackerConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OrgId",
                table: "TrackerConnections",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Down is lossy where Up was not, and cannot be otherwise: an enum has no member for a
            // provider added after this migration, so such a connection comes back as 0. That is
            // the cost of returning to an enum, and part of the reason not to have one.
            migrationBuilder.Sql($"""
                UPDATE [VcsConnections]
                SET [VcsType] = CASE LOWER(LTRIM(RTRIM([ProviderType]))) WHEN 'gitlab' THEN {LegacyGitLab} ELSE 0 END;
                """);

            migrationBuilder.Sql($"""
                UPDATE [TrackerConnections]
                SET [TrackerType] = CASE LOWER(LTRIM(RTRIM([ProviderType]))) WHEN 'yandextracker' THEN {LegacyYandexTracker} ELSE 0 END;
                """);

            migrationBuilder.Sql("""
                UPDATE [TrackerConnections]
                SET [OrgId] = LEFT(JSON_VALUE([ProviderSettingsJson], '$.orgId'), 200)
                WHERE ISJSON([ProviderSettingsJson]) = 1;
                """);

            migrationBuilder.DropColumn(name: "ProviderType", table: "VcsConnections");
            migrationBuilder.DropColumn(name: "ProviderType", table: "TrackerConnections");
            migrationBuilder.DropColumn(name: "ProviderSettingsJson", table: "TrackerConnections");
        }
    }
}
