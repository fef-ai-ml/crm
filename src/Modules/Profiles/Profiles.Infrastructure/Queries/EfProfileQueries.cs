using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Paging;
using Crm.Profiles.Application.Queries;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure.Queries;

/// <summary>
/// Read side over ProfilesDbContext. The tenant predicate is the context's global filter; "MergedIntoId is null" is
/// added here on every profile read (FR-09). Lists are keyset-paged: one row more than the page is fetched to learn
/// whether a next page exists, and the cursor is the last returned row's (sort timestamp, id) (rulings F2, F4).
/// </summary>
internal sealed class EfProfileQueries(ProfilesDbContext db) : IProfileQueries
{
    private const string SearchScope = "profiles";
    private const string TimelineScope = "events";

    public async Task<Result<ProfileListResponse>> SearchAsync(string? q, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        var term = ProfileSearchTerm.Parse(q);
        if (term.IsFailure)
        {
            return term.Error!;
        }

        var query = db.Profiles.AsNoTracking().TagWith("profiles-search").Where(p => p.MergedIntoId == null);

        if (!term.Value.IsEmpty)
        {
            var text = term.Value.TextPattern;
            var phone = term.Value.PhonePattern;
            query = phone is null
                ? query.Where(p => EF.Functions.Like(p.ExternalId!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Email!, text, ProfileSearchTerm.LikeEscape))
                : query.Where(p => EF.Functions.Like(p.ExternalId!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Email!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Phone!, phone, ProfileSearchTerm.LikeEscape));
        }

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(SearchScope, cursor, out var after))
            {
                return PagingErrors.InvalidCursor;
            }

            var at = after.Timestamp;
            var id = after.Id;
            // Conjunctive form (ruling G3): one contiguous range on the sort column, seekable without relying on the
            // optimizer. The disjunctive form it replaces (sort < at || (sort == at && rowId < cursorId)) returns
            // the same rows; measured on SQL Server 2022 it compiles to an equally good two-range seek, so this
            // form is a portability choice, not a measured speed-up.
            query = query.Where(p => p.LastSeenAt <= at && (p.LastSeenAt < at || p.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(p => p.LastSeenAt).ThenByDescending(p => p.Id)
            .Take(size.Value + 1)
            .Select(p => new ProfileListItem(p.Id, p.ExternalId, p.Email, p.Phone, p.LastSeenAt, p.EventCount))
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value).Select(r => r with { LastSeenAt = Utc(r.LastSeenAt) }).ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].LastSeenAt, page[^1].Id).Encode(SearchScope) : null;
        return new ProfileListResponse(page, next);
    }

    public async Task<Result<ProfileResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Profiles.AsNoTracking()
            .Where(p => p.Id == id && p.MergedIntoId == null)
            .Select(p => new { p.Id, p.ExternalId, p.Email, p.Phone, p.Attributes, p.FirstSeenAt, p.LastSeenAt, p.EventCount })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return ProfilesErrors.NotFound;
        }

        return new ProfileResponse(row.Id, row.ExternalId, row.Email, row.Phone, ToElement(row.Attributes), Utc(row.FirstSeenAt), Utc(row.LastSeenAt), row.EventCount);
    }

    public async Task<Result<TimelineResponse>> TimelineAsync(Guid id, TimelineFilter filter, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        KeysetCursor? after = null;
        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(TimelineScope, cursor, out var decoded))
            {
                return PagingErrors.InvalidCursor;
            }

            after = decoded;
        }

        if (!await db.Profiles.AsNoTracking().AnyAsync(p => p.Id == id && p.MergedIntoId == null, cancellationToken))
        {
            return ProfilesErrors.NotFound;
        }

        var query = db.Events.AsNoTracking().TagWith("profiles-timeline").Where(e => e.ProfileId == id);

        if (filter.Name is { } name)
        {
            query = query.Where(e => e.Name == name);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        if (after is { } position)
        {
            var at = position.Timestamp;
            var lastId = position.Id;
            query = query.Where(e => e.OccurredAt <= at && (e.OccurredAt < at || e.Id.CompareTo(lastId) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(size.Value + 1)
            .Select(e => new { e.Id, e.Name, e.Properties, e.OccurredAt, e.ReceivedAt })
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(e => new TimelineEvent(e.Id, e.Name, ToElement(e.Properties), Utc(e.OccurredAt), Utc(e.ReceivedAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].OccurredAt, page[^1].Id).Encode(TimelineScope) : null;
        return new TimelineResponse(page, next);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>The columns carry an ISJSON check constraint, so the text always parses; Clone detaches it from the document.</summary>
    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
