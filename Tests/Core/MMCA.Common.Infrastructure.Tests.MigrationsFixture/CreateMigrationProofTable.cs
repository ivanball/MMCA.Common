using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Tests.MigrationsFixture;

/// <summary>
/// A real EF Core migration for the framework's single SQLite context, kept in this tiny library
/// so that the migration-apply proof runs against something a consumer would actually commit.
/// </summary>
/// <remarks>
/// It lives OUTSIDE the test assembly on purpose. EF selects migrations by the
/// <see cref="DbContextAttribute"/> they carry and the framework declares exactly one SQLite
/// context class (ADR-006), so a migration compiled into
/// <c>MMCA.Common.Infrastructure.Tests</c> would immediately be "pending" for every other test that
/// names the test assembly as its migrations assembly, notably
/// <c>DbContextFactoryMigrationTargetTests</c>, whose whole argument rests on that assembly
/// declaring none. Only the tests that opt in by naming THIS assembly ever see this migration.
/// No Designer file and no model snapshot accompany it: neither is needed to apply a migration at
/// run time, and both exist only to let <c>dotnet ef migrations add</c> diff the next one.
/// </remarks>
[DbContext(typeof(SqliteDbContext))]
[Migration(MigrationId)]
public sealed class CreateMigrationProofTable : Migration
{
    /// <summary>The migration identifier, as recorded in <c>__EFMigrationsHistory</c>.</summary>
    public const string MigrationId = "20260831000001_CreateMigrationProofTable";

    /// <summary>The table this migration creates, and the evidence that it was applied.</summary>
    public const string TableName = "MigrationProof";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        // Column types are stated explicitly rather than inferred: with no model snapshot the
        // migration carries an empty target model, so nothing else would supply them.
        migrationBuilder.CreateTable(
            name: TableName,
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey($"PK_{TableName}", x => x.Id));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.DropTable(name: TableName);
    }
}
