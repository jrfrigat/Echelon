using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.MsSql.Migrations
{
    /// <inheritdoc />
    public partial class PlanRecordsItsOwnOrder : Migration
    {
        /// <summary>
        /// Drops whatever default constraint sits on <c>RolloutPlans.Version</c>, if any.
        /// </summary>
        /// <remarks>
        /// A default constraint blocks DROP COLUMN, and both directions of this migration have to
        /// drop that column. Neither can name the constraint: an inline DEFAULT is auto-named by SQL
        /// Server (DF__RolloutPl__Versi__4A8310C6), and even the named one is dropped again once the
        /// backfill is done -- so a direction that assumed it exists failed on the second cycle. The
        /// catalog is the only reliable answer to "is there one, and what is it called".
        /// </remarks>
        private const string DropVersionDefault = @"
            DECLARE @constraint sysname = (
                SELECT d.name FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
                WHERE d.parent_object_id = OBJECT_ID('RolloutPlans') AND c.name = 'Version');
            IF @constraint IS NOT NULL
                EXEC('ALTER TABLE [RolloutPlans] DROP CONSTRAINT [' + @constraint + ']');";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntraTaskOrder",
                table: "PlanItems");

            // Version stops being a yyyyMMddHHmmss label and becomes an ordinal per target task.
            // Scaffolded as an ALTER, which would fail on every existing row: those labels are
            // fourteen-digit numbers and int stops at ten. Dropped and rebuilt instead, then
            // renumbered from CreatedAt -- which is the fact the old label was really carrying, and
            // is kept in its own column.
            migrationBuilder.Sql(DropVersionDefault);
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] DROP COLUMN [Version];");
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] ADD [Version] int NOT NULL CONSTRAINT [DF_RolloutPlan_Version] DEFAULT 0;");
            migrationBuilder.Sql(@"
                WITH ordered AS (
                    SELECT [Id], ROW_NUMBER() OVER (
                        PARTITION BY [TargetTaskId] ORDER BY [CreatedAt], [Id]) AS [Ordinal]
                    FROM [RolloutPlans])
                UPDATE p SET p.[Version] = o.[Ordinal]
                FROM [RolloutPlans] p INNER JOIN ordered o ON o.[Id] = p.[Id];");
            // The default existed only to let a NOT NULL column be added to populated rows. Dropped
            // so the schema matches the model, which declares no default: the planner assigns every
            // version explicitly, and a lingering DEFAULT 0 would quietly accept a plan that did not.
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] DROP CONSTRAINT [DF_RolloutPlan_Version];");

            migrationBuilder.AddColumn<string>(
                name: "DependsOnTaskIdsJson",
                table: "PlanTaskNodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Wave",
                table: "PlanItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "UX_RolloutPlan_TargetTaskId_Version",
                table: "RolloutPlans",
                columns: new[] { "TargetTaskId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_RolloutPlan_TargetTaskId_Version",
                table: "RolloutPlans");

            migrationBuilder.DropColumn(
                name: "DependsOnTaskIdsJson",
                table: "PlanTaskNodes");

            migrationBuilder.DropColumn(
                name: "Wave",
                table: "PlanItems");

            // The old timestamp labels cannot be reconstructed -- nothing recorded them but the
            // column itself. Rolling back restores the SHAPE and leaves the values empty; CreatedAt
            // still says when each version was built.
            migrationBuilder.Sql(DropVersionDefault);
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] DROP COLUMN [Version];");
            // Named, then dropped: an inline DEFAULT gets an auto-generated name, and leaving one
            // behind would differ from what a build from scratch produces.
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] ADD [Version] nvarchar(50) NOT NULL CONSTRAINT [DF_RolloutPlan_Version_Legacy] DEFAULT '';");
            migrationBuilder.Sql(@"ALTER TABLE [RolloutPlans] DROP CONSTRAINT [DF_RolloutPlan_Version_Legacy];");

            migrationBuilder.AddColumn<int>(
                name: "IntraTaskOrder",
                table: "PlanItems",
                type: "int",
                nullable: true);
        }
    }
}
