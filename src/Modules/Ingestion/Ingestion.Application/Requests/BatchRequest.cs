using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>One flat shape for both item kinds; `type` ("track" | "identify") selects which fields and rules apply.</summary>
public sealed record BatchItem(string? Type, string? UserId, string? AnonymousId, string? Event, JsonElement? Properties, JsonElement? Traits, DateTimeOffset? Timestamp)
{
    public TrackRequest ToTrack() => new(UserId, AnonymousId, Event, Properties, Timestamp);

    public IdentifyRequest ToIdentify() => new(UserId, AnonymousId, Traits);
}

/// <summary>Body of POST /v1/batch.</summary>
public sealed record BatchRequest(IReadOnlyList<BatchItem?>? Events);
