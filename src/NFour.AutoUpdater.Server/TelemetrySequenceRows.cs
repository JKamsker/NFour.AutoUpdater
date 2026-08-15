namespace NFour.AutoUpdater.Server;

/// <summary>Stores a consented telemetry event.</summary>
public sealed class TelemetryRow
{
    /// <summary>Gets or sets the database identity.</summary>
    public long Id { get; set; }
    /// <summary>Gets or sets when the event was received.</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>Gets or sets the optional product identifier.</summary>
    public string? ProductId { get; set; }
    /// <summary>Gets or sets the optional release identifier.</summary>
    public string? ReleaseId { get; set; }
    /// <summary>Gets or sets the serialized event payload.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>Stores the next monotonic sequence value for one named scope.</summary>
public sealed class SequenceReservationRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the sequence scope.</summary>
    public required string Scope { get; set; }
    /// <summary>Gets or sets the name within the sequence scope.</summary>
    public required string Name { get; set; }
    /// <summary>Gets or sets the next value to allocate.</summary>
    public long NextValue { get; set; }
    /// <summary>Gets or sets legacy serialized allocation history.</summary>
    public string? AllocatedSequencesJson { get; set; }
}

/// <summary>
/// Stores one allocated sequence value without rewriting an unbounded serialized array.
/// </summary>
public sealed class SequenceClaimRow
{
    /// <summary>Gets or sets the repository identifier.</summary>
    public required string RepositoryId { get; set; }
    /// <summary>Gets or sets the sequence scope.</summary>
    public required string Scope { get; set; }
    /// <summary>Gets or sets the name within the sequence scope.</summary>
    public required string Name { get; set; }
    /// <summary>Gets or sets the allocated sequence value.</summary>
    public long Value { get; set; }
    /// <summary>Gets or sets when the value was allocated.</summary>
    public DateTimeOffset AllocatedAt { get; set; }
}
