using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NFour.AutoUpdater.Server.Migrations;

[Migration("20260805183000_SequenceClaims")]
[DbContext(typeof(ManagementDbContext))]
public partial class SequenceClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep existing rows null: they predate claim tracking and are handled
        // by the legacy monotonic-counter compatibility path.
        migrationBuilder.AddColumn<string>(name: "AllocatedSequencesJson", table: "SequenceReservations", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "AllocatedSequencesJson", table: "SequenceReservations");
}
