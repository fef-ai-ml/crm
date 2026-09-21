using System.Globalization;
using System.Text;

namespace Crm.Segmentation.Application.Compilation;

/// <summary>Parameterized T-SQL produced by <see cref="RuleCompiler"/>: `Sql` never contains a user-supplied
/// value, only `@tenant` and `@p0…` placeholders (docs/design/architecture.md §6).</summary>
public sealed record CompiledQuery(string Sql, IReadOnlyList<SqlParameterValue> Parameters)
{
    /// <summary>`Sql`, a blank line, then one `@name = &lt;literal&gt;` line per parameter in order
    /// (docs/test/fixtures.md "Golden SQL Files") — the text golden files and audit logs both compare against.</summary>
    public string ToAuditText()
    {
        var builder = new StringBuilder();
        builder.Append(Sql).Append("\n\n");
        foreach (var parameter in Parameters)
        {
            builder.Append(parameter.Name).Append(" = ").Append(FormatLiteral(parameter)).Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatLiteral(SqlParameterValue parameter) => parameter.Kind switch
    {
        SqlValueKind.Text => $"'{((string)parameter.Value).Replace("'", "''", StringComparison.Ordinal)}'",
        SqlValueKind.Float => ((double)parameter.Value).ToString("R", CultureInfo.InvariantCulture),
        SqlValueKind.Int => ((int)parameter.Value).ToString(CultureInfo.InvariantCulture),
        SqlValueKind.DateTime => ((DateTime)parameter.Value).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        SqlValueKind.Guid => ((Guid)parameter.Value).ToString("D"),
        _ => throw new NotSupportedException($"Unknown SQL value kind '{parameter.Kind}'."),
    };
}
