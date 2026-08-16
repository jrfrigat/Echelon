using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations.ArchiveDb
{
    /// <inheritdoc />
    public partial class AddArchivedMergeRequestLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Labels",
                table: "ArchivedMergeRequests",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Labels",
                table: "ArchivedMergeRequests");
        }
    }
}
