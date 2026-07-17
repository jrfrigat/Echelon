using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReleaseOrchestrator.Migrations.MsSql.Migrations
{
    /// <summary>
    /// Makes a permission grant unique, so revoking one actually revokes it.
    /// </summary>
    /// <remarks>
    /// Scaffolding this warns about data loss, for the UserId shrink from 450 to 36. Both halves
    /// are safe on any database this can meet: UserIdentifier.TryNormalize parses a GUID or
    /// rejects the input, so no stored value is longer than 36 characters, and the schema has
    /// never been applied anywhere, so no duplicate grant exists for the unique indexes to
    /// choke on.
    ///
    /// If it ever does meet one — restored from a backup that predates this — the index creation
    /// fails loudly rather than dropping a row, which is the right way round: two grants of the
    /// same claim need a human to decide, not a migration.
    /// </remarks>
    public partial class PermissionGrantUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "UserPermissionOverrides",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.CreateIndex(
                name: "IX_UserPermissionOverride_UserId_PermissionClaimId",
                table: "UserPermissionOverrides",
                columns: new[] { "UserId", "PermissionClaimId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupPermissionMapping_AdGroupSid_PermissionClaimId",
                table: "GroupPermissionMappings",
                columns: new[] { "AdGroupSid", "PermissionClaimId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserPermissionOverride_UserId_PermissionClaimId",
                table: "UserPermissionOverrides");

            migrationBuilder.DropIndex(
                name: "IX_GroupPermissionMapping_AdGroupSid_PermissionClaimId",
                table: "GroupPermissionMappings");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "UserPermissionOverrides",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(36)",
                oldMaxLength: 36);
        }
    }
}
