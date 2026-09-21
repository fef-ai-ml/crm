using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The plan SQL Server compiled for a statement the API really executed (ruling G1): the statement is found in the plan
/// cache by the comment EF Core's TagWith puts in front of it. Needs VIEW SERVER STATE — the test container runs as sa.
/// </summary>
internal sealed class QueryPlan(string xml, string engine)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private readonly XDocument _document = XDocument.Parse(xml);

    public string Xml { get; } = xml;

    /// <summary>First line of `SELECT @@VERSION` at the moment the plan was read. Engine-sensitive assertions append
    /// this to their failure message so an engine/CU change is a one-glance diagnosis instead of a baffling red
    /// (docs/test/integration.md).</summary>
    public string Engine { get; } = engine;

    public IReadOnlyList<string> PhysicalOperators
        => _document.Descendants(Ns + "RelOp").Select(op => (string)op.Attribute("PhysicalOp")!).ToList();

    /// <summary>Index names the plan touches, without brackets.</summary>
    public IReadOnlyList<string> Indexes
        => _document.Descendants(Ns + "Object")
            .Select(o => ((string?)o.Attribute("Index"))?.Trim('[', ']'))
            .Where(name => name is not null).Select(name => name!).Distinct().ToList();

    /// <summary>Columns a seek predicate actually ranges on — SeekPredicates//RangeColumns, not merely referenced
    /// somewhere under SeekPredicates. RangeExpressions (the bind values/parameters) is deliberately excluded so a
    /// parameter name is never mistaken for a seek column. This is what tells a cursor seek (ranges on the sort
    /// column) apart from a tenant-range seek with the cursor evaluated as a residual predicate — an operator list
    /// alone cannot: both shapes report "Index Seek". It does not tell the conjunctive cursor form (ruling G3) from
    /// the disjunctive one: measured on SQL Server 2022, both range on the sort column.</summary>
    public IReadOnlyList<string> SeekColumns
        => _document.Descendants(Ns + "SeekPredicates")
            .Descendants(Ns + "RangeColumns")
            .Descendants(Ns + "ColumnReference")
            .Select(c => (string?)c.Attribute("Column"))
            .Where(name => name is not null).Select(name => name!).Distinct().ToList();

    /// <summary>A Top-N sort is PhysicalOp "Sort" with LogicalOp "TopN Sort"; both count.</summary>
    public bool HasSort => PhysicalOperators.Contains("Sort");
}

internal static class QueryPlans
{
    /// <summary>
    /// The plan of the newest cached statement carrying the tag, or null if none is cached (I2-B: callers assert
    /// inside an AssertionScope rather than let this throw, so a miss does not discard failures the scope already
    /// collected). Several statement shapes share one tag (browse, a cursor page, a search all carry
    /// "profiles-search"); <paramref name="containing"/> is a second LIKE pattern that narrows to the one intended —
    /// "last execution wins" alone can tie at the DMV's 3.33 ms clock granularity (I2-A). Pass null only when the
    /// statement just executed is provably the only one of its tag that could have run since the last read (e.g. the
    /// first read of a fresh test).
    /// </summary>
    public static async Task<QueryPlan?> LatestAsync(string connectionString, string tag, string? containing = null)
    {
        const string sql = """
            SELECT TOP (1) CONVERT(nvarchar(max), qp.query_plan)
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE st.text LIKE @pattern AND st.text LIKE @containing AND st.text NOT LIKE N'%dm_exec_query_stats%' AND qp.query_plan IS NOT NULL
            ORDER BY qs.last_execution_time DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pattern", $"%-- {tag}%");
        command.Parameters.AddWithValue("@containing", containing is null ? "%" : $"%{containing}%");
        var xml = await command.ExecuteScalarAsync() as string;
        if (xml is null)
        {
            return null;
        }

        return new QueryPlan(xml, await EngineAsync(connection));
    }

    private static async Task<string> EngineAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT @@VERSION", connection);
        var version = (string?)await command.ExecuteScalarAsync() ?? "(unknown engine)";
        return version.Split('\n')[0].Trim();
    }
}
