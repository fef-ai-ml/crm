using Crm.Kernel.Time;
using Crm.Segmentation.Domain.Rules;

namespace Crm.Segmentation.Application.Compilation;

/// <summary>Compiles a validated <see cref="RuleNode"/> tree into parameterized T-SQL against
/// `profiles.Profiles` / `profiles.Events` (docs/design/architecture.md §6, ADR-0009). No user-supplied value
/// ever appears in <see cref="CompiledQuery.Sql"/> — only as a <see cref="SqlParameterValue"/> — and every
/// attribute key is re-checked against <see cref="RuleLimits.KeyPattern"/> even though <see cref="RuleParser"/>
/// already enforced it, because this compiler must not trust a tree it did not itself validate.</summary>
public sealed class RuleCompiler(IClock clock)
{
    public CompiledQuery Compile(RuleNode tree, Guid tenantId)
    {
        var context = new CompilationContext();
        context.Parameters.Add(new SqlParameterValue("@tenant", SqlValueKind.Guid, tenantId));

        var predicate = CompileNode(tree, context);
        var sql = $"SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND ({predicate})";

        return new CompiledQuery(sql, context.Parameters);
    }

    private string CompileNode(RuleNode node, CompilationContext context) => node switch
    {
        GroupNode group => CompileGroup(group, context),
        AttributeNode attribute => CompileAttribute(attribute, context),
        EventNode @event => CompileEvent(@event, context),
        _ => throw new NotSupportedException($"Unknown rule node type '{node.GetType()}'."),
    };

    private string CompileGroup(GroupNode group, CompilationContext context)
    {
        var children = group.Children.Select(child => CompileNode(child, context)).ToList();

        return group.Op switch
        {
            GroupOp.And => $"({string.Join(" AND ", children)})",
            GroupOp.Or => $"({string.Join(" OR ", children)})",
            GroupOp.Not => $"NOT ({children[0]})",
            _ => throw new NotSupportedException($"Unknown group operator '{group.Op}'."),
        };
    }

    private static string CompileAttribute(AttributeNode attribute, CompilationContext context)
    {
        var column = ResolveColumn(attribute.Key);

        return attribute.Cmp switch
        {
            AttributeCmp.Exists => $"{column} IS NOT NULL",
            AttributeCmp.NotExists => $"{column} IS NULL",
            AttributeCmp.Contains => CompileContains(column, attribute.Value!, context),
            AttributeCmp.Eq => CompileEquality(column, attribute.Value!, context, negate: false),
            AttributeCmp.Neq => CompileEquality(column, attribute.Value!, context, negate: true),
            AttributeCmp.In => CompileMembership(column, attribute.Value!, context, negate: false),
            AttributeCmp.Nin => CompileMembership(column, attribute.Value!, context, negate: true),
            AttributeCmp.Gt or AttributeCmp.Gte or AttributeCmp.Lt or AttributeCmp.Lte => CompileOrdering(column, attribute, context),
            _ => throw new NotSupportedException($"Unknown attribute comparator '{attribute.Cmp}'."),
        };
    }

    private static string CompileContains(string column, RuleValue value, CompilationContext context)
    {
        var text = ((RuleValue.Text)value).Value;
        var name = context.AddParameter(SqlValueKind.Text, EscapeLikeValue(text));
        return $"{column} LIKE '%' + {name} + '%' ESCAPE '\\'";
    }

    private static string CompileEquality(string column, RuleValue value, CompilationContext context, bool negate)
    {
        var (expr, name) = CompileScalarComparand(column, value, context);
        return negate ? $"({expr} <> {name} OR {expr} IS NULL)" : $"{expr} = {name}";
    }

    private static string CompileMembership(string column, RuleValue value, CompilationContext context, bool negate)
    {
        var (expr, names) = CompileListComparand(column, value, context);
        var list = string.Join(", ", names);
        return negate ? $"({expr} NOT IN ({list}) OR {expr} IS NULL)" : $"{expr} IN ({list})";
    }

    private static string CompileOrdering(string column, AttributeNode attribute, CompilationContext context)
    {
        var op = attribute.Cmp switch
        {
            AttributeCmp.Gt => ">",
            AttributeCmp.Gte => ">=",
            AttributeCmp.Lt => "<",
            AttributeCmp.Lte => "<=",
            _ => throw new NotSupportedException($"'{attribute.Cmp}' is not an ordering comparator."),
        };

        var value = ((RuleValue.Number)attribute.Value!).Value;
        var name = context.AddParameter(SqlValueKind.Float, value);
        return $"TRY_CONVERT(float, {column}) {op} {name}";
    }

    // Numeric-typed values wrap the column in TRY_CONVERT regardless of comparator (H17); a boolean compares
    // as the string 'true'/'false'; anything else compares as text.
    private static (string Expr, string Name) CompileScalarComparand(string column, RuleValue value, CompilationContext context) => value switch
    {
        RuleValue.Number number => ($"TRY_CONVERT(float, {column})", context.AddParameter(SqlValueKind.Float, number.Value)),
        RuleValue.Bool boolean => (column, context.AddParameter(SqlValueKind.Text, boolean.Value ? "true" : "false")),
        RuleValue.Text text => (column, context.AddParameter(SqlValueKind.Text, text.Value)),
        _ => throw new NotSupportedException($"'{value.GetType()}' cannot compare by equality."),
    };

    private static (string Expr, IReadOnlyList<string> Names) CompileListComparand(string column, RuleValue value, CompilationContext context) => value switch
    {
        RuleValue.NumberList numbers => ($"TRY_CONVERT(float, {column})", numbers.Values.Select(v => context.AddParameter(SqlValueKind.Float, v)).ToList()),
        RuleValue.TextList texts => (column, texts.Values.Select(v => context.AddParameter(SqlValueKind.Text, v)).ToList()),
        _ => throw new NotSupportedException($"'{value.GetType()}' is not a list value."),
    };

    private string CompileEvent(EventNode @event, CompilationContext context)
    {
        var nameParameter = context.AddParameter(SqlValueKind.Text, @event.Name);
        var since = clock.UtcNow - @event.Window.Duration;
        var sinceParameter = context.AddParameter(SqlValueKind.DateTime, since);
        var subquery = $"SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = {nameParameter} AND e.OccurredAt >= {sinceParameter}";

        return @event.Cmp switch
        {
            EventCmp.Eq when @event.Count == 0 => $"NOT EXISTS ({subquery})",
            EventCmp.Eq => $"EXISTS ({subquery} HAVING COUNT(*) = {context.AddParameter(SqlValueKind.Int, @event.Count)})",
            EventCmp.Gte => $"EXISTS ({subquery} HAVING COUNT(*) >= {context.AddParameter(SqlValueKind.Int, @event.Count)})",
            EventCmp.Lte => $"NOT EXISTS ({subquery} HAVING COUNT(*) > {context.AddParameter(SqlValueKind.Int, @event.Count)})",
            _ => throw new NotSupportedException($"Unknown event comparator '{@event.Cmp}'."),
        };
    }

    // Defense in depth: RuleParser already enforced this pattern while building the tree, but this compiler
    // must not trust that the tree it is handed came from RuleParser (task-3-brief.md).
    private static string ResolveColumn(string key)
    {
        if (!RuleLimits.KeyPattern().IsMatch(key))
        {
            throw new ArgumentException($"Attribute key '{key}' does not match the allowed pattern ^[A-Za-z0-9_]{{1,64}}$.", nameof(key));
        }

        return PromotedAttributeColumns.TryGetColumn(key, out var column) ? $"p.{column}" : $"JSON_VALUE(p.Attributes, '$.{key}')";
    }

    // \, %, _ and [ are LIKE/ESCAPE metacharacters; \ must be escaped first so escaping the others does not
    // double-escape the backslashes it just introduced.
    private static string EscapeLikeValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    private sealed class CompilationContext
    {
        private int _counter;

        public List<SqlParameterValue> Parameters { get; } = [];

        public string AddParameter(SqlValueKind kind, object value)
        {
            var name = $"@p{_counter++}";
            Parameters.Add(new SqlParameterValue(name, kind, value));
            return name;
        }
    }
}
