namespace NFour.AutoUpdater.Core;

/// <summary>Resolves conditional package requirements for a variant selection.</summary>
public interface IVariantResolver
{
    /// <summary>Resolves a release lock for the requested variant.</summary>
    /// <param name="release">The release lock to resolve.</param>
    /// <param name="requested">The requested axis selection.</param>
    /// <returns>The selected packages and any diagnostics.</returns>
    ResolutionResult Resolve(ReleaseLock release, VariantSelection requested);
}

/// <summary>Provides package manifests and their file-table entries.</summary>
public interface IPackageRepository
{
    /// <summary>Gets a package manifest by identifier and version.</summary>
    /// <param name="id">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The manifest, or <see langword="null"/> when it does not exist.</returns>
    ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default);
    /// <summary>Streams the entries referenced by a package manifest.</summary>
    /// <param name="manifest">The manifest whose file table is read.</param>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The asynchronous sequence of file entries.</returns>
    IAsyncEnumerable<PackageFileEntry> ReadFileTableAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>Provides package manifests only when their exact bytes match an expected digest.</summary>
public interface IManifestDigestRepository
{
    /// <summary>Gets and verifies a package manifest.</summary>
    /// <param name="id">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="expectedDigest">The required digest of the exact manifest bytes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The verified manifest, or <see langword="null"/> when it does not exist.</returns>
    ValueTask<PackageManifest?> GetManifestAsync(PackageId id, PackageVersion version, ContentHash expectedDigest, CancellationToken cancellationToken = default);
}

/// <summary>Repository implementations that can expose the immutable bytes used for a manifest digest.</summary>
public interface IExactManifestRepository
{
    /// <summary>Gets the exact immutable bytes for a package manifest.</summary>
    /// <param name="id">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The manifest bytes, or <see langword="null"/> when the manifest does not exist.</returns>
    ValueTask<byte[]?> GetManifestBytesAsync(PackageId id, PackageVersion version, CancellationToken cancellationToken = default);
}

/// <summary>Composes resolved package file tables into one deterministic install set.</summary>
public interface IFileSetComposer
{
    /// <summary>Composes the files contributed by a package resolution.</summary>
    /// <param name="repository">The package repository.</param>
    /// <param name="resolution">The resolved packages.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The composed file set and diagnostics.</returns>
    ValueTask<ComposedFileSet> ComposeAsync(IPackageRepository repository, ResolutionResult resolution, CancellationToken cancellationToken = default);
}

