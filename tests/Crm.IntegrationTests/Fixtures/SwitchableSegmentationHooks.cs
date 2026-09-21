using Crm.Segmentation.Application;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The shared stack's segmentation hooks (task-6 brief). Inert until a test installs a callback; a rewrite
/// callback receives the final statement text so it can inject T-SQL ahead of it (IT-015's `WAITFOR DELAY`) or
/// simply count calls (IT-014's "one `SELECT COUNT(*)`" proof, ruling H21). Dispose the returned handle to
/// uninstall, and call <see cref="Reset"/> in a test class's DisposeAsync so nothing leaks into the next class.
/// </summary>
public sealed class SwitchableSegmentationHooks : ISegmentationHooks
{
    private Func<SegmentQueryKind, string, string>? _rewriteQuery;
    private Func<Guid, CancellationToken, Task>? _membersInserted;

    public string RewriteQuery(SegmentQueryKind kind, string sql)
        => _rewriteQuery?.Invoke(kind, sql) ?? sql;

    public Task MembersInsertedAsync(Guid runId, CancellationToken cancellationToken)
        => _membersInserted?.Invoke(runId, cancellationToken) ?? Task.CompletedTask;

    public IDisposable OnRewriteQuery(Func<SegmentQueryKind, string, string> callback)
    {
        _rewriteQuery = callback;
        return new Uninstall(() => _rewriteQuery = null);
    }

    public IDisposable OnMembersInserted(Func<Guid, CancellationToken, Task> callback)
    {
        _membersInserted = callback;
        return new Uninstall(() => _membersInserted = null);
    }

    public void Reset()
    {
        _rewriteQuery = null;
        _membersInserted = null;
    }

    private sealed class Uninstall(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
