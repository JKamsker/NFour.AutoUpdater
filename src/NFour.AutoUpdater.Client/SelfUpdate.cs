namespace NFour.AutoUpdater.Client;

/// <summary>Configures side-by-side installation of an updater version.</summary>
public sealed record SelfUpdateOptions
{
    /// <summary>Gets the updater installation root.</summary>
    public required string InstallRoot { get; init; }
    /// <summary>Gets the version identifier.</summary>
    public required string Version { get; init; }
    /// <summary>Gets the source directory containing the new updater.</summary>
    public required string SourceDirectory { get; init; }
    /// <summary>Gets an optional state directory copied beside the updater.</summary>
    public string? StateDirectory { get; init; }
    /// <summary>Gets whether a directory symlink is preferred for the current pointer.</summary>
    public bool PreferDirectorySymlink { get; init; } = true;
}

/// <summary>Reports the installed version directory and active pointer.</summary><param name="VersionDirectory">The installed version directory.</param><param name="CurrentPointer">The active launcher pointer.</param><param name="UsedSymlink">Whether the pointer is a directory symlink.</param>
public sealed record SelfUpdateResult(string VersionDirectory, string CurrentPointer, bool UsedSymlink);

/// <summary>Installs an updater beside the running binary and atomically switches the launcher pointer.</summary>
public sealed class SelfUpdateManager
{
    /// <summary>Copies and activates one side-by-side updater version.</summary>
    public async ValueTask<SelfUpdateResult> InstallAsync(SelfUpdateOptions options, CancellationToken cancellationToken = default)
    {
        if (!Identifier.IsValid(options.Version, "version", out var error)) throw new FormatException(error);
        var root = Path.GetFullPath(options.InstallRoot);
        var source = Path.GetFullPath(options.SourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        RejectReparse(source);
        if (!FileIdentityProvider.TryGet(root, out _)) throw new DirectoryNotFoundException(root);

        const string bin = "bin";
        SecureInstallOperations.TryEnsureDirectory(root, new VirtualPath(bin));
        var versionName = options.Version;
        var versionRelative = $"{bin}/{versionName}";
        var versionDirectory = Path.Combine(root, bin, versionName);
        var suffix = 0;
        while (Directory.Exists(versionDirectory) || File.Exists(versionDirectory))
        {
            RejectReparse(versionDirectory);
            suffix++;
            versionName = options.Version + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            versionRelative = $"{bin}/{versionName}";
            versionDirectory = Path.Combine(root, bin, versionName);
        }

        var temporaryName = versionName + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var temporaryRelative = $"{bin}/{temporaryName}";
        var temporaryDirectory = Path.Combine(root, bin, temporaryName);
        try
        {
            SecureInstallOperations.TryEnsureDirectory(root, new VirtualPath(temporaryRelative));
            CopyTree(source, root, temporaryRelative, cancellationToken);
            if (!string.IsNullOrWhiteSpace(options.StateDirectory))
            {
                var stateSource = Path.GetFullPath(options.StateDirectory);
                if (Directory.Exists(stateSource))
                {
                    RejectReparse(stateSource);
                    CopyTree(stateSource, root, temporaryRelative + "/state", cancellationToken);
                }
            }

            // The directory rename is within the trusted bin parent and never overwrites
            // an existing version. It is the commit point for the copied updater.
            Directory.Move(temporaryDirectory, versionDirectory);
            var pointer = Path.Combine(root, bin, "current");
            var pointerText = Path.Combine(root, bin, "current.txt");
            var usedSymlink = false;
            if (options.PreferDirectorySymlink && !OperatingSystem.IsWindows())
            {
                var temporaryPointer = pointer + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                try
                {
                    Directory.CreateSymbolicLink(temporaryPointer, versionDirectory);
                    File.Move(temporaryPointer, pointer, true);
                    usedSymlink = true;
                }
                catch (UnauthorizedAccessException) { TryDeleteLink(temporaryPointer); }
                catch (IOException) { TryDeleteLink(temporaryPointer); }
            }
            if (!usedSymlink)
            {
                var textTemporary = Path.Combine(Path.GetTempPath(), "4sup-current-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                try
                {
                    await File.WriteAllTextAsync(textTemporary, versionName + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    if (!SecureInstallOperations.TryReplaceFile(root, new VirtualPath($"{bin}/current.txt"), textTemporary))
                        throw new IOException("The platform does not support secure self-update pointer replacement.");
                }
                finally { TryDeleteLink(textTemporary); }
            }
            return new SelfUpdateResult(versionDirectory, usedSymlink ? pointer : pointerText, usedSymlink);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                RejectReparse(temporaryDirectory);
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void CopyTree(string source, string installRoot, string destinationRelative, CancellationToken cancellationToken)
    {
        SecureInstallOperations.TryEnsureDirectory(installRoot, new VirtualPath(destinationRelative));
        var pending = new Stack<(string Source, string Relative)>();
        pending.Push((source, string.Empty));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, relative) = pending.Pop();
            RejectReparse(current);
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                RejectReparse(directory);
                var childRelative = CombineRelative(destinationRelative, relative, Path.GetFileName(directory));
                SecureInstallOperations.TryEnsureDirectory(installRoot, new VirtualPath(childRelative));
                pending.Push((directory, CombineRelative(relative, Path.GetFileName(directory))));
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                RejectReparse(file);
                var childRelative = CombineRelative(destinationRelative, relative, Path.GetFileName(file));
                if (!VirtualPath.TryCreate(childRelative.Replace(Path.DirectorySeparatorChar, '/'), out var virtualPath, out var error)) throw new FormatException(error);
                if (!SecureInstallOperations.TryReplaceFile(installRoot, virtualPath, file)) throw new IOException($"The platform does not support secure self-update writes for '{childRelative}'.");
            }
        }
    }

    private static string CombineRelative(params string[] parts) => string.Join('/', parts.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.Replace('\\', '/')));

    private static void RejectReparse(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"Self-update refuses a reparse point: '{path}'.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
