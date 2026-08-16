using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PlanRecordsItsOwnOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntraTaskOrder",
                table: "PlanItems");

            // Version stops being a yyyyMMddHHmmss label and becomes an ordinal per target task.
            // Scaffolded as an ALTER, which would fail on every existing row: those labels are
            // fourteen-digit numbers and integer stops at ten. Dropped and rebuilt instead, then
            // renumbered from CreatedAt -- which is the fact the old label was really carrying, and
            // is kept in its own column.
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" DROP COLUMN ""Version"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" ADD COLUMN ""Version"" integer NOT NULL DEFAULT 0;");
            migrationBuilder.Sql(@"
                UPDATE ""RolloutPlans"" p SET ""Version"" = o.""Ordinal""
                FROM (SELECT ""Id"", ROW_NUMBER() OVER (
                          PARTITION BY ""TargetTaskId"" ORDER BY ""CreatedAt"", ""Id"") AS ""Ordinal""
                      FROM ""RolloutPlans"") o
                WHERE o.""Id"" = p.""Id"";");
            // The default existed only to let a NOT NULL column be added to populated rows. Dropped
            // so the schema matches the model, which declares no default: the planner assigns every
            // version explicitly, and a lingering DEFAULT 0 would quietly accept a plan that did not.
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" ALTER COLUMN ""Version"" DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "DependsOnTaskIdsJson",
                table: "PlanTaskNodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Wave",
                table: "PlanItems",
                type: "integer",
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
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" DROP COLUMN ""Version"";");
            // Added with a default so it can populate existing rows, then dropped, mirroring Up. In
            // PostgreSQL a default goes away with its column, so unlike SQL Server it would not block
            // a re-apply -- keeping the two migrations symmetrical is what stops the schemas drifting.
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" ADD COLUMN ""Version"" character varying(50) NOT NULL DEFAULT '';");
            migrationBuilder.Sql(@"ALTER TABLE ""RolloutPlans"" ALTER COLUMN ""Version"" DROP DEFAULT;");

            migrationBuilder.AddColumn<int>(
                name: "IntraTaskOrder",
                table: "PlanItems",
                type: "integer",
                nullable: true);
        }
    }
}
