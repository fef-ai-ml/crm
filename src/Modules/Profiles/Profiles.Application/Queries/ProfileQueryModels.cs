using System.Text.Json;
using System.Text.RegularExpressions;
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>Search row: no attributes — that document is uncapped and belongs to the detail endpoint (ruling F7).</summary>
public sealed record ProfileListItem(Guid Id, string? ExternalId, string? Email, string? Phone, DateTime LastSeenAt, long EventCount);

public sealed record ProfileListResponse(IReadOnlyList<ProfileListItem> Items, string? NextCursor);

public sealed record ProfileResponse(Guid Id, string? ExternalId, string? Email, string? Phone, JsonElement Attributes, DateTime FirstSeenAt, DateTime LastSeenAt, long EventCount);

public sealed record TimelineEvent(Guid Id, string Name, JsonElement Properties, DateTime OccurredAt, DateTime ReceivedAt);

public sealed record TimelineResponse(IReadOnlyList<TimelineEvent> Items, string? NextCursor);

/// <summary>`from` inclusive, `to` exclusive, both UTC; `name` is an exact event name (ruling F9).</summary>
public sealed partial record TimelineFilter(DateTime? FromUtc, DateTime? ToUtc, string? Name)
{
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    public static Result<TimelineFilter> Create(DateTimeOffset? from, DateTimeOffset? to, string? name)
    {
        if (name is not null && !EventName().IsMatch(name))
        {
            return ProfilesErrors.InvalidFilter("name", "must match [A-Za-z0-9_]{1,64}");
        }

        if (from is not null && to is not null && from.Value >= to.Value)
        {
            return ProfilesErrors.InvalidFilter("from", "must be earlier than to");
        }

        return new TimelineFilter(from?.UtcDateTime, to?.UtcDateTime, name);
    }
}
