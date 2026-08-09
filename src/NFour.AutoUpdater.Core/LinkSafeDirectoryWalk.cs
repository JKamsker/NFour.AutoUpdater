namespace NFour.AutoUpdater.Core;

/// <summary>One entry found by <see cref="LinkSafeDirectoryWalk"/>.</summary>
/// <param name="Path">Absolute path of the entry.</param>
/// <param name="IsLink">
/// True when the entry itself is a symlink, junction or other reparse point.  The walk never
/// descends into such an entry; it is reported so callers can surface it rather than having
/// it disappear silently.
/// </param>
public readonly record struct WalkedEntry(string Path, bool IsLink);

/// <summary>
/// Directory traversal that never descends through a symlink, junction or other reparse
/// point.
///
/// <c>SearchOption.AllDirectories</c> walks the filesystem namespace, following links as it
/// goes.  Code that enumerates recursively and then checks only the leaf entry for
/// <see cref="FileAttributes.ReparsePoint"/> therefore accepts ordinary files whose real
/// location is outside the tree being walked, because the link was an ancestor and the leaf
/// itself is a perfectly normal file.  For a traversal that defines a trust boundary — what
/// may be published, what may be listed as a store object — every level has to be checked,
/// which means walking it explicitly.
/// </summary>
public static class LinkSafeDirectoryWalk
{
    /// Files reachable from <paramref name="root"/> without crossing a link, each flagged
    /// with whether the file itself is a link.
    public static IEnumerable<WalkedEntry> EnumerateFileEntries(string root)
    {
        foreach (var (directory, _) in Walk(root))
        {
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return new WalkedEntry(file, IsLink(file));
        }
    }

    /// Directories reachable from <paramref name="root"/> without crossing a link, excluding
    /// <paramref name="root"/> itself.  Linked directories are reported but not descended into.
    public static IEnumerable<WalkedEntry> EnumerateDirectoryEntries(string root)
    {
        foreach (var (directory, isRoot) in Walk(root))
        {
            if (!isRoot) yield return new WalkedEntry(directory, false);
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (IsLink(child)) yield return new WalkedEntry(child, true);
        }
    }

    /// Files reachable without crossing a link, excluding links themselves.
    public static IEnumerable<string> EnumerateFiles(string root)
        => EnumerateFileEntries(root).Where(static x => !x.IsLink).Select(static x => x.Path);

    /// <summary>True when the path exists and is a link of any kind.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
            // Unix symlinks carry no ReparsePoint attribute, so LinkTarget is also consulted.
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static IEnumerable<(string Directory, bool IsRoot)> Walk(string root)
    {
        if (!Directory.Exists(root) || IsLink(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return (current, string.Equals(current, root, StringComparison.Ordinal));
            string[] children;
            try { children = Directory.GetDirectories(current); }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (!IsLink(child)) pending.Push(child);
        }
    }
}
