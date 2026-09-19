using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>Body of POST /v1/identify.</summary>
public sealed record IdentifyRequest(string? UserId = null, string? AnonymousId = null, JsonElement? Traits = null);
