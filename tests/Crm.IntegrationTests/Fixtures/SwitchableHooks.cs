using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The shared Worker's pipeline hooks. Inert until a test installs a callback; callbacks receive the tenant or the
/// record, so a test reacts to its own data only. Dispose the returned handle to uninstall.
/// </summary>
public sealed class SwitchableHooks : IPipelineHooks
{
    private Func<Guid, CancellationToken, Task>? _mergeLocksTaken;
    private Func<RawEnvelope, CancellationToken, Task>? _sqlCommitted;

    public Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken)
        => _mergeLocksTaken?.Invoke(tenantId, cancellationToken) ?? Task.CompletedTask;

    public Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken)
        => _sqlCommitted?.Invoke(envelope, cancellationToken) ?? Task.CompletedTask;

    public IDisposable OnMergeLocksTaken(Func<Guid, CancellationToken, Task> callback)
    {
        _mergeLocksTaken = callback;
        return new Uninstall(() => _mergeLocksTaken = null);
    }

    public IDisposable OnSqlCommitted(Func<RawEnvelope, CancellationToken, Task> callback)
    {
        _sqlCommitted = callback;
        return new Uninstall(() => _sqlCommitted = null);
    }

    private sealed class Uninstall(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
