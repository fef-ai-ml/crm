using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>Body of POST /v1/track. Everything is nullable so a missing field reaches the validator instead of failing deserialization.</summary>
public sealed record TrackRequest(string? UserId = null, string? AnonymousId = null, string? Event = null, JsonElement? Properties = null, DateTimeOffset? Timestamp = null);
