namespace FourSaas.AutoUpdater.Core;

/// <summary>
/// Parsing and validation for the four-digit octal file modes carried in file-table rows.
///
/// Modes come from a publisher and are applied to files on an end user's machine, so the
/// grammar is an allowlist rather than a filter: exactly four octal digits, and the leading
/// digit — setuid, setgid and sticky — must be zero. A package that could request setuid
/// would be requesting privilege escalation on every machine that installs it, and no
/// legitimate use of this updater needs it. Rejecting at parse time means neither the
/// publisher nor the client can express one.
/// </summary>
public static class PosixFileMode
{
    /// <summary>Mode applied to executable-policy files when the manifest declares none.</summary>
    public const uint DefaultExecutable = 0x1ED; // 0755

    /// <summary>Mode applied to ordinary files when the manifest declares none.</summary>
    public const uint DefaultRegular = 0x1A4; // 0644

    public static bool TryParse(string? text, out uint mode, out string? error)
    {
        mode = 0;
        error = null;
        if (text is null) { error = "A file mode is required."; return false; }
        if (text.Length != 4 || text.Any(static c => c is < '0' or > '7'))
        {
            error = $"File mode '{text}' must be exactly four octal digits.";
            return false;
        }
        if (text[0] != '0')
        {
            error = $"File mode '{text}' sets setuid, setgid or the sticky bit, which is not permitted.";
            return false;
        }
        mode = Convert.ToUInt32(text, 8);
        return true;
    }

    /// <summary>Formats permission bits back into the canonical four-digit representation.</summary>
    public static string Format(uint mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0');

    /// <summary>
    /// Resolves the mode to apply for a file, preferring an explicit manifest mode and
    /// otherwise falling back to the policy default.
    /// </summary>
    public static uint Resolve(string? declaredMode, FileInstallPolicy policy)
    {
        if (declaredMode is not null && TryParse(declaredMode, out var parsed, out _)) return parsed;
        return policy == FileInstallPolicy.Executable ? DefaultExecutable : DefaultRegular;
    }
}
