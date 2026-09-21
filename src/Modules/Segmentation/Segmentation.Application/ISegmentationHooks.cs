namespace Crm.Segmentation.Application;

/// <summary>
/// Test seams for the SQL this module executes against `profiles.Profiles` / `profiles.Events` (task-6 brief).
/// Production uses <see cref="NoSegmentationHooks"/>. <see cref="RewriteQuery"/> receives and returns the final
/// statement text (after `SELECT COUNT(*) FROM (...) q` wrapping, etc.), so a test can prefix it with T-SQL
/// (IT-015's `WAITFOR DELAY`) or simply observe it (IT-014's "one `SELECT COUNT(*)`" proof, ruling H21).
/// <see cref="MembersInsertedAsync"/> is a pause point for the batch runner a later task adds.
/// </summary>
public interface ISegmentationHooks
{
    string RewriteQuery(SegmentQueryKind kind, string sql);

    Task MembersInsertedAsync(Guid runId, CancellationToken cancellationToken);
}

internal sealed class NoSegmentationHooks : ISegmentationHooks
{
    public string RewriteQuery(SegmentQueryKind kind, string sql) => sql;

    public Task MembersInsertedAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
}
