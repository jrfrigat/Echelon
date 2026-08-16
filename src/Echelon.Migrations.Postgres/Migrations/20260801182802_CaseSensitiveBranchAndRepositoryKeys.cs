using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echelon.Migrations.Postgres.Migrations
{
    /// <summary>
    /// Deliberately empty. The SQL Server side of this migration forces
    /// <c>Latin1_General_100_BIN2</c> onto <c>RepositoryBranches.Name</c> and
    /// <c>Repositories.ExternalId</c>, because a SQL Server instance's default collation is normally
    /// case-insensitive and both columns sit under a unique index - so two branches differing only in
    /// case collide (confirmed: <c>Msg 2601</c>). PostgreSQL already compares text case-sensitively,
    /// so there is nothing to change here.
    ///
    /// It exists rather than being skipped so the two providers' migration histories stay aligned:
    /// a name present on one and absent on the other is how the next mirrored pair gets scaffolded
    /// against the wrong baseline. Do not delete it as dead weight.
    /// </summary>
    public partial class CaseSensitiveBranchAndRepositoryKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
