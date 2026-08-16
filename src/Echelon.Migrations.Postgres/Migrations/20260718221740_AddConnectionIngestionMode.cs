using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionIngestionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IngestionMode",
                table: "VcsConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalSeconds",
                table: "VcsConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IngestionMode",
                table: "VcsConnections");

            migrationBuilder.DropColumn(
                name: "PollIntervalSeconds",
                table: "VcsConnections");
        }
    }
}
