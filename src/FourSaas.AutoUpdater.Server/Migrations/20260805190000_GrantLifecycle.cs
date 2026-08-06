using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FourSaas.AutoUpdater.Server.Migrations;

[Migration("20260805190000_GrantLifecycle")]
[DbContext(typeof(ManagementDbContext))]
public partial class GrantLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Status", table: "PublishGrants", type: "text", nullable: false, defaultValue: "issued");
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ClaimedAt", table: "PublishGrants", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "ConsumedAt", table: "PublishGrants", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "InvalidatedAt", table: "PublishGrants", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "MultipartUploadId", table: "PublishGrants", type: "text", nullable: true);
        migrationBuilder.AddColumn<long>(name: "MultipartPartSize", table: "PublishGrants", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "MultipartCompleted", table: "PublishGrants", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "MultipartCompleting", table: "PublishGrants", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(name: "MultipartPartsJson", table: "PublishGrants", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Status", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "ClaimedAt", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "ConsumedAt", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "InvalidatedAt", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "MultipartUploadId", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "MultipartPartSize", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "MultipartCompleted", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "MultipartCompleting", table: "PublishGrants");
        migrationBuilder.DropColumn(name: "MultipartPartsJson", table: "PublishGrants");
    }
}
