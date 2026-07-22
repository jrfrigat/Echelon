using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class VcsConnectionProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderSettingsJson",
                table: "VcsConnections",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderSettingsJson",
                table: "VcsConnections");
        }
    }
}
