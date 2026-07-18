using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryDependency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepositoryDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromRepositoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToRepositoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryDependencies_Repositories_FromRepositoryId",
                        column: x => x.FromRepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepositoryDependencies_Repositories_ToRepositoryId",
                        column: x => x.ToRepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryDependencies_ToRepositoryId",
                table: "RepositoryDependencies",
                column: "ToRepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryDependency_From_To",
                table: "RepositoryDependencies",
                columns: new[] { "FromRepositoryId", "ToRepositoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryDependencies");
        }
    }
}
