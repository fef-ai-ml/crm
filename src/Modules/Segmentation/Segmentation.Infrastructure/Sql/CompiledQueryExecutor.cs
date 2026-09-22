using System.Data;
using System.Data.Common;
using System.Globalization;
using Crm.Segmentation.Application;
using Crm.Segmentation.Application.Compilation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Sql;

/// <summary>
/// The only class in this module that executes <see cref="RuleCompiler"/>-generated SQL text (task-6 brief);
/// everywhere else, `profiles.Profiles` / `profiles.Events` are reached through EF Core or not at all (AT-007).
/// Parameters are typed explicitly from <see cref="SqlValueKind"/>, never `AddWithValue`, so a value's SQL type
/// is exactly what the compiler intended rather than whatever ADO.NET infers from the .NET type.
/// </summary>
internal sealed class CompiledQueryExecutor(SegmentationDbContext db, ISegmentationHooks hooks)
{
    private const int InlineTextMaxLength = 4000;

    /// <summary>
    /// Wraps <paramref name="query"/> as `SELECT COUNT(*) FROM (...) q`, passes that whole statement through
    /// <see cref="ISegmentationHooks.RewriteQuery"/>, then executes it against the current
    /// <see cref="SegmentationDbContext"/> connection with a <paramref name="timeout"/> command budget that
    /// covers both opening the connection (a cache-miss preview usually finds it closed) and running the
    /// command, as one bounded operation. Throws <see cref="SqlException"/> (Number -2) or an
    /// <see cref="OperationCanceledException"/> tied to the budget's own token when that budget elapses; a
    /// cancellation of <paramref name="cancellationToken"/> itself propagates unchanged, so a client disconnect
    /// is never mistaken for a timeout.
    /// </summary>
    public async Task<int> CountAsync(CompiledQuery query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = hooks.RewriteQuery(SegmentQueryKind.Preview, $"SELECT COUNT(*) FROM ({query.Sql}) q");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        // M-7: `openedHere` is computed from the connection's state, so `finally` below only ever closes what
        // this method itself opened. Both of today's callers (preview, IT-042) call this outside any EF
        // transaction and on their own scope, so the connection is never shared with a concurrent caller; if a
        // future caller ever wraps this in an EF transaction on the same context, `openedHere` would be false
        // here and this method would correctly leave the connection alone — but two callers racing on the very
        // same context's connection is not a case this state check is atomic against. Not a problem today (only
        // these two callers exist, and neither shares a context across concurrent calls).
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(budget.Token);
        }

        try
        {
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // text comes from RuleCompiler, which emits validated keys and parameters only (UT-037, AT-007)
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = (int)timeout.TotalSeconds;

            foreach (var parameter in query.Parameters)
            {
                command.Parameters.Add(ToSqlParameter(parameter));
            }

            var result = await command.ExecuteScalarAsync(budget.Token);
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// The run's member-insert (task-7 brief): `INSERT INTO segments.SegmentRunMembers (...) SELECT @tenant,
    /// @run, q.Id FROM (&lt;compiled&gt;) q`, passed through <see cref="ISegmentationHooks.RewriteQuery"/> with
    /// <see cref="SegmentQueryKind.Run"/>, executed on the run's own <paramref name="transaction"/> — never a
    /// connection of its own — so it shares the run's single transaction and command budget (H6).
    /// <paramref name="query"/>'s own parameters already include `@tenant` (<see cref="RuleCompiler.Compile"/>);
    /// only `@run` is added here. Returns the number of members inserted.
    /// </summary>
    public async Task<int> InsertMembersAsync(CompiledQuery query, Guid runId, DbTransaction transaction, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(transaction);

        var sql = hooks.RewriteQuery(
            SegmentQueryKind.Run,
            $"INSERT INTO segments.SegmentRunMembers (TenantId, RunId, ProfileId) SELECT @tenant, @run, q.Id FROM ({query.Sql}) q");

        var connection = (SqlConnection)transaction.Connection!;
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // text comes from RuleCompiler, which emits validated keys and parameters only (UT-037, AT-007)
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = (SqlTransaction)transaction;
        command.CommandTimeout = (int)timeout.TotalSeconds;

        foreach (var parameter in query.Parameters)
        {
            command.Parameters.Add(ToSqlParameter(parameter));
        }

        command.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = runId });

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqlParameter ToSqlParameter(SqlParameterValue value) => value.Kind switch
    {
        SqlValueKind.Text => TextParameter(value),
        SqlValueKind.Float => new SqlParameter(value.Name, SqlDbType.Float) { Value = value.Value },
        SqlValueKind.Int => new SqlParameter(value.Name, SqlDbType.Int) { Value = value.Value },
        SqlValueKind.DateTime => new SqlParameter(value.Name, SqlDbType.DateTime2) { Value = value.Value },
        SqlValueKind.Guid => new SqlParameter(value.Name, SqlDbType.UniqueIdentifier) { Value = value.Value },
        _ => throw new NotSupportedException($"Unknown SQL value kind '{value.Kind}'."),
    };

    private static SqlParameter TextParameter(SqlParameterValue value)
    {
        var text = (string)value.Value;
        var size = text.Length > InlineTextMaxLength ? -1 : InlineTextMaxLength;
        return new SqlParameter(value.Name, SqlDbType.NVarChar, size) { Value = text };
    }
}
