using System.Text.Json.Serialization;

namespace Crm.Ingestion.Application.Requests;

public sealed record IngestAccepted(Guid EventId);

/// <summary>Exactly one of EventId (accepted) or Errors (rejected) is present.</summary>
public sealed record BatchItemResult(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EventId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors);

public sealed record BatchAccepted(int Accepted, int Rejected, IReadOnlyList<BatchItemResult> Results);
