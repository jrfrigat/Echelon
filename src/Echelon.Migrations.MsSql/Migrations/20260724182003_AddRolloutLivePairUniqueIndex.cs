using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRolloutLivePairUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Rollout_TargetTask_Environment_Live",
                table: "Rollouts",
                columns: new[] { "TargetTaskId", "EnvironmentId" },
                unique: true,
                filter: "[Status] <> 5 AND [Status] <> 6 AND [Status] <> 7");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Rollout_TargetTask_Environment_Live",
                table: "Rollouts");
        }
    }
}
