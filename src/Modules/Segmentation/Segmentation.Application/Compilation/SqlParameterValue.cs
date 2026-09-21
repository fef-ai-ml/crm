using System.Diagnostics.CodeAnalysis;

namespace Crm.Segmentation.Application.Compilation;

/// <summary>The .NET type a <see cref="SqlParameterValue"/> carries, and how <see cref="CompiledQuery.ToAuditText"/>
/// formats its literal.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Member names name the carried .NET type by design (task-3-brief.md).")]
public enum SqlValueKind { Text, Float, Int, DateTime, Guid }

/// <summary>One parameter of a <see cref="CompiledQuery"/>. <see cref="Name"/> includes the leading `@`.</summary>
public sealed record SqlParameterValue(string Name, SqlValueKind Kind, object Value);
