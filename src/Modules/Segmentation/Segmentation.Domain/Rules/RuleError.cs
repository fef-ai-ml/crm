namespace Crm.Segmentation.Domain.Rules;

/// <summary>A single validation failure. <see cref="Path"/> is `definition` for the root, `&lt;parent&gt;.children[i]` below, or a field path such as `definition.key` (H19).</summary>
public sealed record RuleError(string Path, string Message);

/// <summary>Result of <see cref="RuleParser.Parse"/>. <see cref="Tree"/> is populated only when <see cref="IsValid"/>.</summary>
public sealed record RuleParseResult(RuleNode? Tree, IReadOnlyList<RuleError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
