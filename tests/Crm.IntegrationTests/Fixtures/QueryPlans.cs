using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The plan SQL Server compiled for a statement the API really executed (ruling G1): the statement is found in the plan
/// cache by the comment EF Core's TagWith puts in front of it. Needs VIEW SERVER STATE — the test container runs as sa.
/// </summary>
internal sealed class QueryPlan(string xml)
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private readonly XDocument _document = XDocument.Parse(xml);

    public string Xml { get; } = xml;

    public IReadOnlyList<string> PhysicalOperators
        => _document.Descendants(Ns + "RelOp").Select(op => (string)op.Attribute("PhysicalOp")!).ToList();

    /// <summary>Index names the plan touches, without brackets.</summary>
    public IReadOnlyList<string> Indexes
        => _document.Descendants(Ns + "Object")
            .Select(o => ((string?)o.Attribute("Index"))?.Trim('[', ']'))
            .Where(name => name is not null).Select(name => name!).Distinct().ToList();

    /// <summary>A Top-N sort is PhysicalOp "Sort" with LogicalOp "TopN Sort"; both count.</summary>
    public bool HasSort => PhysicalOperators.Contains("Sort");
}

internal static class QueryPlans
{
    public static async Task<QueryPlan> LatestAsync(string connectionString, string tag)
    {
        const string sql = """
            SELECT TOP (1) CONVERT(nvarchar(max), qp.query_plan)
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE st.text LIKE @pattern AND st.text NOT LIKE N'%dm_exec_query_stats%' AND qp.query_plan IS NOT NULL
            ORDER BY qs.last_execution_time DESC
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@pattern", $"%-- {tag}%");
        var xml = await command.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"No cached plan for a statement tagged '{tag}'. Was the endpoint called, and does the query carry TagWith(\"{tag}\")?");
        return new QueryPlan(xml);
    }
}
