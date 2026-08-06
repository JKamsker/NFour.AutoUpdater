namespace FourSaas.AutoUpdater.Client;

/// <summary>
/// Per-kernel values for the POSIX interop used by the install adapters.
///
/// The numeric flags below are not standardised by POSIX: only the names are.  Linux and
/// Darwin disagree on every one of O_CREAT, O_EXCL, O_NOFOLLOW, O_DIRECTORY, O_CLOEXEC,
/// AT_FDCWD and AT_REMOVEDIR, so a single hardcoded table silently produces the wrong
/// open semantics on the other kernel — the O_NOFOLLOW that this client's security model
/// depends on would simply not be requested.  Every call site takes its constants here.
///
/// errno values (ENOENT, ENOTDIR, EEXIST) and the permission bits do agree, so those stay
/// as plain constants at their use sites.
/// </summary>
internal static class PosixPlatform
{
    private static readonly bool IsDarwin = OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

    public const int O_RDONLY = 0;
    public const int O_WRONLY = 1;

    public static int O_CREAT => IsDarwin ? 0x0200 : 0x40;
    public static int O_EXCL => IsDarwin ? 0x0800 : 0x80;
    public static int O_NOFOLLOW => IsDarwin ? 0x0100 : 0x20000;
    public static int O_DIRECTORY => IsDarwin ? 0x100000 : 0x10000;
    public static int O_CLOEXEC => IsDarwin ? 0x1000000 : 0x80000;

    public static int AT_FDCWD => IsDarwin ? -2 : -100;
    public static int AT_REMOVEDIR => IsDarwin ? 0x80 : 0x200;

    /// <summary>Directory opened read-only, no-follow, close-on-exec.</summary>
    public static int DirectoryOpenFlags => O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC;

    /// <summary>Regular file opened read-only, no-follow, close-on-exec.</summary>
    public static int FileReadFlags => O_RDONLY | O_NOFOLLOW | O_CLOEXEC;

    /// <summary>Exclusive create of a new regular file, no-follow, close-on-exec.</summary>
    public static int FileCreateFlags => O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC;
}
