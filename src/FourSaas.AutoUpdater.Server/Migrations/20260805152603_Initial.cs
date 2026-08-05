using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FourSaas.AutoUpdater.Server.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SequenceReservations",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_SequenceReservations", x => new { x.RepositoryId, x.Scope, x.Name }); });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Resource = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlobPlacements",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    Algorithm = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    BackendId = table.Column<string>(type: "text", nullable: false),
                    ObjectKey = table.Column<string>(type: "text", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobPlacements", x => new { x.RepositoryId, x.Algorithm, x.Hash, x.BackendId, x.ObjectKey });
                });

            migrationBuilder.CreateTable(
                name: "BlobRefs",
                columns: table => new
                {
                    Algorithm = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobRefs", x => new { x.Algorithm, x.Hash });
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    ChannelSequence = table.Column<long>(type: "bigint", nullable: false),
                    ReleaseId = table.Column<string>(type: "text", nullable: false),
                    PointerDigest = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => new { x.RepositoryId, x.ProductId, x.Channel });
                });

            migrationBuilder.CreateTable(
                name: "PackageVersions",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    PackageId = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ManifestDigest = table.Column<string>(type: "text", nullable: false),
                    WhenJson = table.Column<string>(type: "jsonb", nullable: true),
                    FileTableJson = table.Column<string>(type: "text", nullable: true),
                    Published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageVersions", x => new { x.RepositoryId, x.PackageId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => new { x.RepositoryId, x.ProductId });
                });

            migrationBuilder.CreateTable(
                name: "PublishGrants",
                columns: table => new
                {
                    GrantId = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    StagingKey = table.Column<string>(type: "text", nullable: false),
                    Digest = table.Column<string>(type: "text", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishGrants", x => x.GrantId);
                });

            migrationBuilder.CreateTable(
                name: "PublishSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxObjects = table.Column<int>(type: "integer", nullable: false),
                    MaxTotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    ObjectCount = table.Column<int>(type: "integer", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sealed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishSessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseDrafts",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    DraftId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseDrafts", x => new { x.RepositoryId, x.DraftId });
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    ReleaseId = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EnvelopeDigest = table.Column<string>(type: "text", nullable: false),
                    CoverageDigest = table.Column<string>(type: "text", nullable: false),
                    Published = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => new { x.RepositoryId, x.ProductId, x.ReleaseId });
                });

            migrationBuilder.CreateTable(
                name: "Revocations",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Revocations", x => new { x.RepositoryId, x.ProductId });
                });

            migrationBuilder.CreateTable(
                name: "Telemetry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: true),
                    ReleaseId = table.Column<string>(type: "text", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Telemetry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrustedKeys",
                columns: table => new
                {
                    RepositoryId = table.Column<string>(type: "text", nullable: false),
                    KeyId = table.Column<string>(type: "text", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedKeys", x => new { x.RepositoryId, x.KeyId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_At",
                table: "AuditEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_BlobPlacements_RepositoryId_Algorithm_Hash_VerifiedAt",
                table: "BlobPlacements",
                columns: new[] { "RepositoryId", "Algorithm", "Hash", "VerifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_RepositoryId_ProductId_ChannelSequence",
                table: "Channels",
                columns: new[] { "RepositoryId", "ProductId", "ChannelSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_RepositoryId_PackageId_Sequence",
                table: "PackageVersions",
                columns: new[] { "RepositoryId", "PackageId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageVersions_WhenJson",
                table: "PackageVersions",
                column: "WhenJson")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseDrafts_PayloadJson",
                table: "ReleaseDrafts",
                column: "PayloadJson")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_RepositoryId_ProductId_Sequence",
                table: "Releases",
                columns: new[] { "RepositoryId", "ProductId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Telemetry_At",
                table: "Telemetry",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "BlobPlacements");

            migrationBuilder.DropTable(
                name: "BlobRefs");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "PackageVersions");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "PublishGrants");

            migrationBuilder.DropTable(
                name: "PublishSessions");

            migrationBuilder.DropTable(
                name: "ReleaseDrafts");

            migrationBuilder.DropTable(
                name: "Releases");

            migrationBuilder.DropTable(
                name: "Revocations");

            migrationBuilder.DropTable(
                name: "Telemetry");

            migrationBuilder.DropTable(
                name: "TrustedKeys");

            migrationBuilder.DropTable(
                name: "SequenceReservations");
        }
    }
}
