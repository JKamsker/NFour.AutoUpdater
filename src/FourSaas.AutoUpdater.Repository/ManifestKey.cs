using FourSaas.AutoUpdater.Core;

namespace FourSaas.AutoUpdater.Repository;

/// <summary>
/// Identifies one package manifest for the exact-byte maps carried into projection writes.
///
/// A package id alone does not identify a manifest: a package has many versions, and every
/// one of them shares the id.  Keying an exact-byte map by id alone therefore both collides
/// on insert and silently attributes one version's bytes to another, so the version label is
/// part of the key.
/// </summary>
public readonly record struct ManifestKey(PackageId Id, string Version)
{
    public static ManifestKey For(PackageManifest manifest) => new(manifest.Id, manifest.Version.Label);
    public static ManifestKey For(PackageId id, PackageVersion version) => new(id, version.Label);
    public override string ToString() => $"{Id}@{Version}";
}
