using Microsoft.EntityFrameworkCore;

namespace NFour.AutoUpdater.Server;

/// <summary>
/// Provides the control plane's durable query and index surface. Signed artifacts and
/// content-addressed bytes remain authoritative in object storage.
/// </summary>
/// <param name="options">Entity Framework database options.</param>
public sealed class ManagementDbContext(DbContextOptions<ManagementDbContext> options) : DbContext(options)
{
    private const string JsonColumnType = "jsonb";
    private const string GeneralizedInvertedIndexMethod = "gin";

    /// <summary>Gets registered products.</summary>
    public DbSet<ProductRow> Products => Set<ProductRow>();
    /// <summary>Gets indexed package versions.</summary>
    public DbSet<PackageVersionRow> PackageVersions => Set<PackageVersionRow>();
    /// <summary>Gets known content-addressed blobs.</summary>
    public DbSet<BlobRefRow> BlobRefs => Set<BlobRefRow>();
    /// <summary>Gets verified blob placements.</summary>
    public DbSet<BlobPlacementRow> BlobPlacements => Set<BlobPlacementRow>();
    /// <summary>Gets release drafts.</summary>
    public DbSet<ReleaseDraftRow> ReleaseDrafts => Set<ReleaseDraftRow>();
    /// <summary>Gets published and staged releases.</summary>
    public DbSet<ReleaseRow> Releases => Set<ReleaseRow>();
    /// <summary>Gets channel pointers.</summary>
    public DbSet<ChannelRow> Channels => Set<ChannelRow>();
    /// <summary>Gets publish sessions.</summary>
    public DbSet<PublishSessionRow> PublishSessions => Set<PublishSessionRow>();
    /// <summary>Gets append-only audit events.</summary>
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    /// <summary>Gets trusted signing keys.</summary>
    public DbSet<TrustedKeyRow> TrustedKeys => Set<TrustedKeyRow>();
    /// <summary>Gets product revocation documents.</summary>
    public DbSet<RevocationRow> Revocations => Set<RevocationRow>();
    /// <summary>Gets publish upload grants.</summary>
    public DbSet<PublishGrantRow> PublishGrants => Set<PublishGrantRow>();
    /// <summary>Gets consented telemetry events.</summary>
    public DbSet<TelemetryRow> Telemetry => Set<TelemetryRow>();
    /// <summary>Gets monotonic sequence counters.</summary>
    public DbSet<SequenceReservationRow> SequenceReservations => Set<SequenceReservationRow>();
    /// <summary>Gets individual allocated sequence claims.</summary>
    public DbSet<SequenceClaimRow> SequenceClaims => Set<SequenceClaimRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductRow>().HasKey(x => new { x.RepositoryId, x.ProductId });
        modelBuilder.Entity<PackageVersionRow>().HasKey(x => new { x.RepositoryId, x.PackageId, x.Version });
        modelBuilder.Entity<PackageVersionRow>().HasIndex(x => new { x.RepositoryId, x.PackageId, x.Sequence }).IsUnique();
        modelBuilder.Entity<PackageVersionRow>().Property(x => x.WhenJson).HasColumnType(JsonColumnType);
        modelBuilder.Entity<PackageVersionRow>().HasIndex(x => x.WhenJson).HasMethod(GeneralizedInvertedIndexMethod);
        modelBuilder.Entity<BlobRefRow>().HasKey(x => new { x.Algorithm, x.Hash });
        modelBuilder.Entity<BlobPlacementRow>().HasKey(x => new { x.RepositoryId, x.Algorithm, x.Hash, x.BackendId, x.ObjectKey });
        modelBuilder.Entity<BlobPlacementRow>().HasIndex(x => new { x.RepositoryId, x.Algorithm, x.Hash, x.VerifiedAt });
        modelBuilder.Entity<ReleaseDraftRow>().HasKey(x => new { x.RepositoryId, x.DraftId });
        modelBuilder.Entity<ReleaseDraftRow>().Property(x => x.PayloadJson).HasColumnType(JsonColumnType);
        modelBuilder.Entity<ReleaseDraftRow>().HasIndex(x => x.PayloadJson).HasMethod(GeneralizedInvertedIndexMethod);
        modelBuilder.Entity<ReleaseRow>().HasKey(x => new { x.RepositoryId, x.ProductId, x.ReleaseId });
        modelBuilder.Entity<ReleaseRow>().HasIndex(x => new { x.RepositoryId, x.ProductId, x.Sequence }).IsUnique();
        modelBuilder.Entity<ChannelRow>().HasKey(x => new { x.RepositoryId, x.ProductId, x.Channel });
        modelBuilder.Entity<ChannelRow>().HasIndex(x => new { x.RepositoryId, x.ProductId, x.Channel, x.ChannelSequence }).IsUnique();
        modelBuilder.Entity<PublishSessionRow>().HasKey(x => x.SessionId);
        modelBuilder.Entity<AuditEventRow>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditEventRow>().HasIndex(x => x.At);
        modelBuilder.Entity<TrustedKeyRow>().HasKey(x => new { x.RepositoryId, x.KeyId });
        modelBuilder.Entity<RevocationRow>().HasKey(x => new { x.RepositoryId, x.ProductId });
        modelBuilder.Entity<PublishGrantRow>().HasKey(x => x.GrantId);
        modelBuilder.Entity<PublishGrantRow>().Property(x => x.MultipartPartsJson).HasColumnType(JsonColumnType);
        // PostgreSQL partitioned tables require a partition key in a primary key.
        // The identity remains globally unique in practice, while (Id, At) is the
        // relational key accepted by every partition.
        modelBuilder.Entity<TelemetryRow>().HasKey(x => new { x.Id, x.At });
        modelBuilder.Entity<TelemetryRow>().HasIndex(x => x.At);
        modelBuilder.Entity<SequenceReservationRow>().HasKey(x => new { x.RepositoryId, x.Scope, x.Name });
        // One row per allocated sequence. The composite primary key is the uniqueness
        // constraint: a duplicate allocation fails on insert rather than being merged into a
        // rewritten array, and membership is a key lookup instead of a scan.
        modelBuilder.Entity<SequenceClaimRow>().HasKey(x => new { x.RepositoryId, x.Scope, x.Name, x.Value });
    }
}
