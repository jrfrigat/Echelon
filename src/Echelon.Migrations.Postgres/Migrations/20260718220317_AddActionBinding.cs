using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddActionBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SettingsJson = table.Column<string>(type: "text", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionBindings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionBinding_EventType",
                table: "ActionBindings",
                column: "EventType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionBindings");
        }
    }
}
