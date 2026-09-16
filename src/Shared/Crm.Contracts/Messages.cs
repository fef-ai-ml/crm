using System.Text.Json;

namespace Crm.Contracts;

/// <summary>Profile fields carried on processed events so consumers never read the database for evaluation.</summary>
public sealed record ProfileSnapshot(string? ExternalId, string? Email, string? Phone, JsonElement Attributes);

/// <summary>events.raw — a track call accepted by the API.</summary>
public sealed record TrackEventRaw(
    Guid EventId,
    Guid TenantId,
    string? UserId,
    string? AnonymousId,
    string Event,
    JsonElement Properties,
    DateTime OccurredAt,
    DateTime ReceivedAt);

/// <summary>events.raw — an identify call accepted by the API.</summary>
public sealed record IdentifyRaw(
    Guid EventId,
    Guid TenantId,
    string UserId,
    string? AnonymousId,
    JsonElement Traits,
    DateTime ReceivedAt);

/// <summary>events.processed — an event appended to a profile timeline.</summary>
public sealed record EventTracked(
    Guid EventId,
    Guid TenantId,
    Guid ProfileId,
    string Event,
    JsonElement Properties,
    DateTime OccurredAt,
    ProfileSnapshot Profile);

/// <summary>events.processed — profile attributes changed (identify or merge).</summary>
public sealed record ProfileUpdated(
    Guid TenantId,
    Guid ProfileId,
    IReadOnlyList<string> ChangedKeys,
    ProfileSnapshot Profile,
    DateTime OccurredAt);

/// <summary>events.processed — two profiles were merged; consumers must re-point cached state.</summary>
public sealed record ProfileMerged(
    Guid TenantId,
    Guid WinnerProfileId,
    Guid LoserProfileId,
    DateTime OccurredAt);
