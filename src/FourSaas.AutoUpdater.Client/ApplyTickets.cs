namespace FourSaas.AutoUpdater.Client;

/// A platform-specific apply hook.  The core engine owns the ordering and guarantees that
/// tickets are restored even when fetch, materialisation, cancellation, or commit fails.
public interface IApplyTicketProvider
{
    ValueTask<IApplyTicketLease> AcquireAsync(string installRoot, IReadOnlyList<FileOperation> operations, CancellationToken cancellationToken = default);
}

/// Optional recovery hook for platform ticket providers.  A provider that mutates
/// processes or services must persist its captured state before the first mutation and
/// replay it on the next invocation after an unclean exit.
public interface IRecoverableApplyTicketProvider
{
    ValueTask RecoverAsync(string installRoot, CancellationToken cancellationToken = default);
}

public interface IApplyTicketLease : IAsyncDisposable
{
    ValueTask RestoreAsync(CancellationToken cancellationToken = default);
}

public sealed class NoopApplyTicketProvider : IApplyTicketProvider
{
    public ValueTask<IApplyTicketLease> AcquireAsync(string installRoot, IReadOnlyList<FileOperation> operations, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IApplyTicketLease>(new NoopApplyTicketLease());

    private sealed class NoopApplyTicketLease : IApplyTicketLease
    {
        public ValueTask RestoreAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
