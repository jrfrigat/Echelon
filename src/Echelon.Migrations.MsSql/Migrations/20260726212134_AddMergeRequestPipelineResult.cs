using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddMergeRequestPipelineResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PipelineResult",
                table: "MergeRequests",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PipelineResult",
                table: "MergeRequests");
        }
    }
}
