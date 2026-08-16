using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryDeployStrategy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeployStrategyKey",
                table: "Repositories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeployStrategySettingsJson",
                table: "Repositories",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeployStrategyKey",
                table: "Repositories");

            migrationBuilder.DropColumn(
                name: "DeployStrategySettingsJson",
                table: "Repositories");
        }
    }
}
