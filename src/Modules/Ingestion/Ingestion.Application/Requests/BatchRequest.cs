using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>One flat shape for both item kinds; `type` ("track" | "identify") selects which fields and rules apply.</summary>
public sealed record BatchItem(string? Type = null, string? UserId = null, string? AnonymousId = null, string? Event = null, JsonElement? Properties = null, JsonElement? Traits = null, DateTimeOffset? Timestamp = null)
{
    public TrackRequest ToTrack() => new(UserId, AnonymousId, Event, Properties, Timestamp);

    public IdentifyRequest ToIdentify() => new(UserId, AnonymousId, Traits);
}

/// <summary>Body of POST /v1/batch.</summary>
public sealed record BatchRequest(IReadOnlyList<BatchItem?>? Events = null);
