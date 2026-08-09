using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFour.AutoUpdater.Server.Migrations;

/// <summary>
/// Replaces the AllocatedSequencesJson array with one row per allocated sequence.
///
/// The array was deserialized, appended to, sorted and rewritten on every allocation, and
/// linearly scanned on every placement, so both cost grew with the number of sequences ever
/// issued for a scope. Existing arrays are expanded into the new table; the column is kept so
/// the pre-claims compatibility path in WasSequenceAllocatedAsync can still tell a legacy
/// reservation (null) from a migrated one (expanded).
/// </summary>
[Migration("20260806140000_NormalizeSequenceClaims")]
[DbContext(typeof(ManagementDbContext))]
public partial class NormalizeSequenceClaims : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SequenceClaims",
            columns: table => new
            {
                RepositoryId = table.Column<string>(type: "text", nullable: false),
                Scope = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Value = table.Column<long>(type: "bigint", nullable: false),
                AllocatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SequenceClaims", x => new { x.RepositoryId, x.Scope, x.Name, x.Value }));

        migrationBuilder.Sql(
            """
            INSERT INTO "SequenceClaims" ("RepositoryId", "Scope", "Name", "Value", "AllocatedAt")
            SELECT r."RepositoryId", r."Scope", r."Name", v::bigint, NOW()
            FROM "SequenceReservations" r
            CROSS JOIN LATERAL jsonb_array_elements_text(r."AllocatedSequencesJson"::jsonb) AS v
            WHERE r."AllocatedSequencesJson" IS NOT NULL
            ON CONFLICT DO NOTHING;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "SequenceClaims");
}
