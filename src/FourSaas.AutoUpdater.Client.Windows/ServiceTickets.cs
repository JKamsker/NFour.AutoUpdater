using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FourSaas.AutoUpdater.Client;

namespace FourSaas.AutoUpdater.Client.Windows;

public enum LockedFilePolicy { Fail, RenameAside, PendingReboot, AskUser }
public enum RestartBehavior { Never, WhenStarted, Always }

public sealed record ServiceTicket(string ServiceName, string OriginalStartType, string OriginalStatus, bool WasRunning, int? SessionId)
{
    public static async ValueTask<ServiceTicket> CaptureAndStopAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows service tickets are only available on Windows.");
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Any(char.IsControl) || serviceName.Contains('"')) throw new ArgumentException("Invalid Windows service name.", nameof(serviceName));
        var configuration = await RunScCaptureAsync($"qc \"{serviceName}\"", cancellationToken).ConfigureAwait(false);
        var state = await RunScCaptureAsync($"query \"{serviceName}\"", cancellationToken).ConfigureAwait(false);
        var startType = ParseStartType(configuration);
        var wasRunning = state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        var originalStatus = wasRunning ? "running" : state.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ? "stopped" : "unknown";
        var ticket = new ServiceTicket(serviceName, startType, originalStatus, wasRunning, Environment.ProcessId == 0 ? null : Process.GetCurrentProcess().SessionId);
        // Manual is set before stop, preventing SCM recovery/start triggers from racing the apply.
        await RunScAsync($"config \"{serviceName}\" start= demand", cancellationToken).ConfigureAwait(false);
        if (wasRunning)
        {
            await RunScAsync($"stop \"{serviceName}\"", cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt != 60; attempt++)
            {
                var status = await RunScCaptureAsync($"query \"{serviceName}\"", cancellationToken).ConfigureAwait(false);
                if (status.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) break;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (attempt == 59) throw new IOException($"Service '{serviceName}' did not reach STOPPED before apply.");
            }
        }
        return ticket;
    }

    public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(ServiceName) || ServiceName.Any(char.IsControl) || ServiceName.Contains('"')) throw new ArgumentException("Invalid Windows service name.", nameof(ServiceName));
        var startType = OriginalStartType.Trim().ToLowerInvariant() switch
        {
            "automatic" or "auto" => "auto",
            "manual" or "demand" => "demand",
            "disabled" => "disabled",
            "boot" => "boot",
            "system" => "system",
            _ => throw new InvalidDataException($"Unknown original service start type '{OriginalStartType}'.")
        };
        await RunScAsync($"config \"{ServiceName}\" start= {startType}", cancellationToken).ConfigureAwait(false);
        if (WasRunning || string.Equals(OriginalStatus, "running", StringComparison.OrdinalIgnoreCase))
            await RunScAsync($"start \"{ServiceName}\"", cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunScAsync(string arguments, CancellationToken cancellationToken)
    {
        _ = await RunScCaptureAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunScCaptureAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new IOException("Unable to start sc.exe.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"sc.exe failed with exit code {process.ExitCode}: {await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)}");
        return await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ParseStartType(string configuration)
    {
        var line = configuration.Split('\n').FirstOrDefault(x => x.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("sc.exe did not report a service start type.");
        var value = line[(line.IndexOf(':') + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return value switch
        {
            "2" => "automatic", "3" => "manual", "4" => "disabled", "0" => "boot", "1" => "system",
            _ => throw new InvalidDataException($"Unsupported service start type '{value}'.")
        };
    }
}

/// Applies only to explicitly configured services.  The updater never guesses which services
/// belong to a product and never terminates an unrelated process.
public sealed class WindowsApplyTicketProvider : IApplyTicketProvider, IRecoverableApplyTicketProvider
{
    private const string TicketFileName = "tickets.json";

    public async ValueTask RecoverAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = GetTicketPath(installRoot);
        if (!File.Exists(path)) return;
        TicketSnapshot snapshot;
        try
        {
            await using var stream = File.OpenRead(path);
            snapshot = await JsonSerializer.DeserializeAsync<TicketSnapshot>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The persisted Windows apply ticket is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The persisted Windows apply ticket cannot be read: {ex.Message}", ex);
        }

        var errors = new List<Exception>();
        foreach (var ticket in snapshot.Services.Reverse())
            try { await ticket.RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        foreach (var ticket in snapshot.Processes.Reverse())
            try { await ticket.RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("One or more Windows apply tickets could not be restored; the durable ticket was retained for retry.", errors);
        File.Delete(path);
    }

    public async ValueTask<IApplyTicketLease> AcquireAsync(string installRoot, IReadOnlyList<FileOperation> operations, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return await new NoopApplyTicketProvider().AcquireAsync(installRoot, operations, cancellationToken).ConfigureAwait(false);
        var configured = Environment.GetEnvironmentVariable("FOURSUP_WINDOWS_SERVICES");
        var processIds = Environment.GetEnvironmentVariable("FOURSUP_WINDOWS_PROCESS_IDS");
        if (string.IsNullOrWhiteSpace(configured) && string.IsNullOrWhiteSpace(processIds)) return await new NoopApplyTicketProvider().AcquireAsync(installRoot, operations, cancellationToken).ConfigureAwait(false);
        var tickets = new List<ServiceTicket>();
        var processes = new List<ProcessTicket>();
        try
        {
            if (!string.IsNullOrWhiteSpace(configured))
                foreach (var service in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tickets.Add(await ServiceTicket.CaptureAndStopAsync(service, cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(processIds))
                foreach (var value in processIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!int.TryParse(value, out var pid) || pid <= 0) throw new FormatException("FOURSUP_WINDOWS_PROCESS_IDS must contain positive process ids.");
                    else processes.Add(await ProcessTicket.CaptureAndStopAsync(pid, cancellationToken).ConfigureAwait(false));
            var ticketPath = GetTicketPath(installRoot);
            await WriteSnapshotAsync(ticketPath, new TicketSnapshot(tickets, processes), cancellationToken).ConfigureAwait(false);
            return new WindowsApplyTicketLease(tickets, processes, ticketPath);
        }
        catch
        {
            foreach (var ticket in tickets.AsEnumerable().Reverse())
            {
                try { await ticket.RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            foreach (var ticket in processes.AsEnumerable().Reverse())
            {
                try { await ticket.RestoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            throw;
        }
    }

    private static string GetTicketPath(string installRoot)
    {
        var metadata = Path.Combine(Path.GetFullPath(installRoot), ".4sup");
        Directory.CreateDirectory(metadata);
        return Path.Combine(metadata, TicketFileName);
    }

    private static async ValueTask WriteSnapshotAsync(string path, TicketSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record TicketSnapshot(IReadOnlyList<ServiceTicket> Services, IReadOnlyList<ProcessTicket> Processes);

    private sealed class WindowsApplyTicketLease(IReadOnlyList<ServiceTicket> tickets, IReadOnlyList<ProcessTicket> processes, string ticketPath) : IApplyTicketLease
    {
        private int _restored;
        public async ValueTask RestoreAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _restored, 1) != 0) return;
            var errors = new List<Exception>();
            foreach (var ticket in tickets.Reverse())
                try { await ticket.RestoreAsync(cancellationToken).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
            foreach (var ticket in processes.Reverse())
                try { await ticket.RestoreAsync(cancellationToken).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count != 0) throw new AggregateException("One or more Windows apply tickets could not be restored; the durable ticket was retained for retry.", errors);
            File.Delete(ticketPath);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// Process tickets are opt-in by PID. The original executable is captured before termination;
/// restoration never guesses from a file lock or a process name.
public sealed record ProcessTicket(int ProcessId, string ExecutablePath, bool WasRunning)
{
    public long StartTimeUtcTicks { get; init; }
    public int SessionId { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;

    public static async ValueTask<ProcessTicket> CaptureAndStopAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows process tickets are only available on Windows.");
        using var process = Process.GetProcessById(processId);
        if (process.Id == Environment.ProcessId) throw new InvalidOperationException("The updater process may not be stopped by its own ticket provider.");
        var executable = process.MainModule?.FileName ?? throw new IOException($"Unable to identify executable for process {processId}.");
        var ticket = new ProcessTicket(processId, executable, !process.HasExited)
        {
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            SessionId = process.SessionId,
            Arguments = ReadArguments(process),
            WorkingDirectory = ReadWorkingDirectory(process)
        };
        if (!process.HasExited)
        {
            // The updater is allowed to stop the explicitly named process, not an
            // arbitrary descendant tree that may contain unrelated user work.
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        return ticket;
    }

    public ValueTask RestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WasRunning || !File.Exists(ExecutablePath)) return ValueTask.CompletedTask;
        try
        {
            using var existing = Process.GetProcessById(ProcessId);
            if (StartTimeUtcTicks != 0 && existing.StartTime.ToUniversalTime().Ticks == StartTimeUtcTicks) return ValueTask.CompletedTask;
            throw new IOException($"Process id {ProcessId} is now owned by a different process; refusing to restore the ticket into a reused pid.");
        }
        catch (ArgumentException) { }
        var startInfo = new ProcessStartInfo { FileName = ExecutablePath, Arguments = Arguments, WorkingDirectory = WorkingDirectory, UseShellExecute = false };
        _ = Process.Start(startInfo);
        return ValueTask.CompletedTask;
    }

    private static string ReadArguments(Process process)
    {
        try { return process.StartInfo.Arguments ?? string.Empty; } catch { return string.Empty; }
    }

    private static string ReadWorkingDirectory(Process process)
    {
        try { return process.StartInfo.WorkingDirectory ?? string.Empty; } catch { return string.Empty; }
    }
}

public interface IWindowsLockInspector
{
    ValueTask<IReadOnlyList<string>> FindHoldersAsync(string path, CancellationToken cancellationToken = default);
}

/// Uses the Windows Restart Manager to identify processes holding a path. It never terminates them.
public sealed class WindowsRestartManagerLockInspector : IWindowsLockInspector
{
    public ValueTask<IReadOnlyList<string>> FindHoldersAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return ValueTask.FromResult<IReadOnlyList<string>>([]);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("A fully-qualified path is required.", nameof(path));

        var key = Guid.NewGuid().ToString("N");
        var result = RmStartSession(out var session, 0, key);
        if (result != 0) throw new IOException($"Restart Manager could not start a session ({result}).");
        try
        {
            var files = new[] { Path.GetFullPath(path) };
            result = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (result != 0) throw new IOException($"Restart Manager could not register '{path}' ({result}).");

            uint needed = 0, count = 0, reasons;
            result = RmGetList(session, out needed, ref count, null, out reasons);
            if (result != 0 && result != 234) throw new IOException($"Restart Manager could not inspect '{path}' ({result}).");
            if (needed == 0) return ValueTask.FromResult<IReadOnlyList<string>>([]);
            var processes = new RM_PROCESS_INFO[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, processes, out reasons);
            if (result != 0) throw new IOException($"Restart Manager could not enumerate holders of '{path}' ({result}).");
            var holders = processes.Take((int)count).Select(x => $"{x.ApplicationName} (pid {x.Process.dwProcessId})").Distinct(StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult<IReadOnlyList<string>>(holders);
        }
        finally { _ = RmEndSession(session); }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[]? files, uint applicationCount, RM_UNIQUE_PROCESS[]? applications, uint serviceCount, string[]? services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint sessionHandle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)] private struct RM_UNIQUE_PROCESS { public int dwProcessId; public SystemTime ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
}

// Kept as a source-compatible name; unlike the old implementation it is platform-aware.
public sealed class NoopWindowsLockInspector : IWindowsLockInspector
{
    private readonly WindowsRestartManagerLockInspector _inner = new();
    public ValueTask<IReadOnlyList<string>> FindHoldersAsync(string path, CancellationToken cancellationToken = default) => _inner.FindHoldersAsync(path, cancellationToken);
}
