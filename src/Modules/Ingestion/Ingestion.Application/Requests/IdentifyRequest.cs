using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>Body of POST /v1/identify.</summary>
public sealed record IdentifyRequest(string? UserId, string? AnonymousId, JsonElement? Traits);
