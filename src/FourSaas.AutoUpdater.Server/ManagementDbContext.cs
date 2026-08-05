using Microsoft.EntityFrameworkCore;

namespace FourSaas.AutoUpdater.Server;

/// The control plane's durable query/index surface.  Signed artifacts and CAS bytes remain
/// authoritative in object storage; these rows deliberately contain references, not file-table
/// contents or a second copy of signed documents.
public sealed class ManagementDbContext(DbContextOptions<ManagementDbContext> options) : DbContext(options)
{
    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<PackageVersionRow> PackageVersions => Set<PackageVersionRow>();
    public DbSet<BlobRefRow> BlobRefs => Set<BlobRefRow>();
    public DbSet<BlobPlacementRow> BlobPlacements => Set<BlobPlacementRow>();
    public DbSet<ReleaseDraftRow> ReleaseDrafts => Set<ReleaseDraftRow>();
    public DbSet<ReleaseRow> Releases => Set<ReleaseRow>();
    public DbSet<ChannelRow> Channels => Set<ChannelRow>();
    public DbSet<PublishSessionRow> PublishSessions => Set<PublishSessionRow>();
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<TrustedKeyRow> TrustedKeys => Set<TrustedKeyRow>();
    public DbSet<RevocationRow> Revocations => Set<RevocationRow>();
    public DbSet<PublishGrantRow> PublishGrants => Set<PublishGrantRow>();
    public DbSet<TelemetryRow> Telemetry => Set<TelemetryRow>();
    public DbSet<SequenceReservationRow> SequenceReservations => Set<SequenceReservationRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductRow>().HasKey(x => new { x.RepositoryId, x.ProductId });
        modelBuilder.Entity<PackageVersionRow>().HasKey(x => new { x.RepositoryId, x.PackageId, x.Version });
        modelBuilder.Entity<PackageVersionRow>().HasIndex(x => new { x.RepositoryId, x.PackageId, x.Sequence }).IsUnique();
        modelBuilder.Entity<PackageVersionRow>().Property(x => x.WhenJson).HasColumnType("jsonb");
        modelBuilder.Entity<PackageVersionRow>().HasIndex(x => x.WhenJson).HasMethod("gin");
        modelBuilder.Entity<BlobRefRow>().HasKey(x => new { x.Algorithm, x.Hash });
        modelBuilder.Entity<BlobPlacementRow>().HasKey(x => new { x.RepositoryId, x.Algorithm, x.Hash, x.BackendId, x.ObjectKey });
        modelBuilder.Entity<BlobPlacementRow>().HasIndex(x => new { x.RepositoryId, x.Algorithm, x.Hash, x.VerifiedAt });
        modelBuilder.Entity<ReleaseDraftRow>().HasKey(x => new { x.RepositoryId, x.DraftId });
        modelBuilder.Entity<ReleaseDraftRow>().Property(x => x.PayloadJson).HasColumnType("jsonb");
        modelBuilder.Entity<ReleaseDraftRow>().HasIndex(x => x.PayloadJson).HasMethod("gin");
        modelBuilder.Entity<ReleaseRow>().HasKey(x => new { x.RepositoryId, x.ProductId, x.ReleaseId });
        modelBuilder.Entity<ReleaseRow>().HasIndex(x => new { x.RepositoryId, x.ProductId, x.Sequence }).IsUnique();
        modelBuilder.Entity<ChannelRow>().HasKey(x => new { x.RepositoryId, x.ProductId, x.Channel });
        modelBuilder.Entity<ChannelRow>().HasIndex(x => new { x.RepositoryId, x.ProductId, x.ChannelSequence }).IsUnique();
        modelBuilder.Entity<PublishSessionRow>().HasKey(x => x.SessionId);
        modelBuilder.Entity<AuditEventRow>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditEventRow>().HasIndex(x => x.At);
        modelBuilder.Entity<TrustedKeyRow>().HasKey(x => new { x.RepositoryId, x.KeyId });
        modelBuilder.Entity<RevocationRow>().HasKey(x => new { x.RepositoryId, x.ProductId });
        modelBuilder.Entity<PublishGrantRow>().HasKey(x => x.GrantId);
        modelBuilder.Entity<TelemetryRow>().HasKey(x => x.Id);
        modelBuilder.Entity<TelemetryRow>().HasIndex(x => x.At);
        modelBuilder.Entity<SequenceReservationRow>().HasKey(x => new { x.RepositoryId, x.Scope, x.Name });
    }
}

public sealed class ProductRow { public required string RepositoryId { get; set; } public required string ProductId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class PackageVersionRow
{
    public required string RepositoryId { get; set; }
    public required string PackageId { get; set; }
    public required string Version { get; set; }
    public long Sequence { get; set; }
    public required string ManifestDigest { get; set; }
    public string? WhenJson { get; set; }
    public string? FileTableJson { get; set; }
    public bool Published { get; set; }
}
public sealed class BlobRefRow { public required string Algorithm { get; set; } public required string Hash { get; set; } public long Length { get; set; } public DateTimeOffset FirstSeenAt { get; set; } }
public sealed class BlobPlacementRow
{
    public required string RepositoryId { get; set; }
    public required string Algorithm { get; set; }
    public required string Hash { get; set; }
    public required string BackendId { get; set; }
    public required string ObjectKey { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}
public sealed class ReleaseDraftRow { public required string RepositoryId { get; set; } public required string DraftId { get; set; } public required string ProductId { get; set; } public required string PayloadJson { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ReleaseRow { public required string RepositoryId { get; set; } public required string ProductId { get; set; } public required string ReleaseId { get; set; } public long Sequence { get; set; } public required string EnvelopeDigest { get; set; } public required string CoverageDigest { get; set; } public bool Published { get; set; } }
public sealed class ChannelRow { public required string RepositoryId { get; set; } public required string ProductId { get; set; } public required string Channel { get; set; } public long ChannelSequence { get; set; } public required string ReleaseId { get; set; } public required string PointerDigest { get; set; } }
public sealed class PublishSessionRow
{
    public required string SessionId { get; set; }
    public required string RepositoryId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int MaxObjects { get; set; }
    public long MaxTotalBytes { get; set; }
    public int ObjectCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Sealed { get; set; }
}
public sealed class AuditEventRow { public long Id { get; set; } public DateTimeOffset At { get; set; } public required string Actor { get; set; } public required string Action { get; set; } public required string Resource { get; set; } public required string Outcome { get; set; } }
public sealed class TrustedKeyRow { public required string RepositoryId { get; set; } public required string KeyId { get; set; } public required string PublicKey { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class RevocationRow { public required string RepositoryId { get; set; } public required string ProductId { get; set; } public required string EnvelopeJson { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class PublishGrantRow
{
    public required string GrantId { get; set; }
    public required string SessionId { get; set; }
    public required string RepositoryId { get; set; }
    public required string StagingKey { get; set; }
    public required string Digest { get; set; }
    public long Length { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
}
public sealed class TelemetryRow { public long Id { get; set; } public DateTimeOffset At { get; set; } public string? ProductId { get; set; } public string? ReleaseId { get; set; } public string? PayloadJson { get; set; } }
public sealed class SequenceReservationRow { public required string RepositoryId { get; set; } public required string Scope { get; set; } public required string Name { get; set; } public long NextValue { get; set; } }
