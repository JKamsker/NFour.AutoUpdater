using FourSaas.AutoUpdater.Client;

namespace FourSaas.AutoUpdater.Client.Windows;

public enum LockedFilePolicy { Fail, RenameAside, PendingReboot, AskUser }
public enum RestartBehavior { Never, WhenStarted, Always }
public sealed record ServiceTicket(string ServiceName, string OriginalStartType, string OriginalStatus, bool WasRunning, int? SessionId)
{
    public ValueTask RestoreAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
public interface IWindowsLockInspector
{
    ValueTask<IReadOnlyList<string>> FindHoldersAsync(string path, CancellationToken cancellationToken = default);
}
public sealed class NoopWindowsLockInspector : IWindowsLockInspector
{
    public ValueTask<IReadOnlyList<string>> FindHoldersAsync(string path, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>([]);
}
