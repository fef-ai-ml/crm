using System.Data;
using System.Globalization;
using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Paging;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Queries;

/// <summary>Read side over SegmentationDbContext. The tenant predicate is the context's global filter. The list
/// is keyset-paged by (CreatedAt, Id) descending (H10) and projects only the columns
/// <c>IX_Segments_TenantId_CreatedAt_Id</c> covers (H11), so the read never touches the base table.</summary>
internal sealed class EfSegmentQueries(SegmentationDbContext db) : ISegmentQueries
{
    private const string ListScope = "segments";
    private const string RunsListScope = "segment-runs";
    private const string MembersScope = "segment-members";
    private const string ChangesScope = "segment-changes";

    /// <summary>H13, measured (task-8 report has the full plan/mutation evidence): the brief's single-statement
    /// `(@after IS NULL OR m.ProfileId &lt; @after)` form does not seek on <c>ProfileId</c> — SQL Server 2022
    /// instead ranges <c>IX_SegmentRunMembers_TenantId_ProfileId</c> on <c>TenantId</c> alone and evaluates
    /// <c>RunId</c>/the cursor as residual predicates (no <c>Sort</c>, because that index happens to already be
    /// ordered by <c>ProfileId</c>, but every row of the tenant's other runs is walked to find the page). Split
    /// into two constant statements instead — one per page shape — so each seeks <c>PK_SegmentRunMembers</c> on
    /// <c>(RunId, ProfileId)</c> directly. Both are constant strings (no interpolation or concatenation): CA2100
    /// never fires, and no test hook rewrite is wired in (module-boundary read of `profiles.Profiles`, AT-007) —
    /// <see cref="Application.SegmentQueryKind.Members"/> exists for one, but calling
    /// <see cref="Application.ISegmentationHooks.RewriteQuery"/> here would mean building the final command text
    /// from that return value, which is no longer a compile-time constant.</summary>
    private const string MembersFirstPageSql =
        """
        -- segments-members
        SELECT TOP (@take) m.ProfileId AS Id, p.ExternalId, p.Email, p.LastSeenAt
        FROM segments.SegmentRunMembers m
        JOIN profiles.Profiles p ON p.TenantId = m.TenantId AND p.Id = m.ProfileId
        WHERE m.RunId = @run AND m.TenantId = @tenant
        ORDER BY m.ProfileId DESC
        """;

    private const string MembersCursorPageSql =
        """
        -- segments-members
        SELECT TOP (@take) m.ProfileId AS Id, p.ExternalId, p.Email, p.LastSeenAt
        FROM segments.SegmentRunMembers m
        JOIN profiles.Profiles p ON p.TenantId = m.TenantId AND p.Id = m.ProfileId
        WHERE m.RunId = @run AND m.TenantId = @tenant AND m.ProfileId < @after
        ORDER BY m.ProfileId DESC
        """;

    public async Task<Result<SegmentListResponse>> ListAsync(string? status, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        SegmentStatus[] statuses;
        if (status is null)
        {
            // H14: Draft and Active only; Archived is hidden unless asked for explicitly.
            statuses = [SegmentStatus.Draft, SegmentStatus.Active];
        }
        else if (Enum.TryParse<SegmentStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            statuses = [parsedStatus];
        }
        else
        {
            return SegmentErrors.InvalidFilter("status", "must be Draft, Active or Archived");
        }

        var query = db.Segments.AsNoTracking().TagWith("segments-list").Where(s => statuses.Contains(s.Status));

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(ListScope, cursor, out var after))
            {
                return PagingErrors.InvalidCursor;
            }

            var at = after.Timestamp;
            var id = after.Id;
            // Conjunctive form (ruling G3, mirrors EfProfileQueries): one contiguous range on the sort column.
            query = query.Where(s => s.CreatedAt <= at && (s.CreatedAt < at || s.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Take(size.Value + 1)
            .Select(s => new { s.Id, s.Name, s.Status, s.MemberCount, s.LastRunAt, s.IsRealtimeEligible, s.CreatedAt })
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(r => new SegmentListItem(r.Id, r.Name, r.Status.ToString(), r.MemberCount, UtcOrNull(r.LastRunAt), r.IsRealtimeEligible, Utc(r.CreatedAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].CreatedAt, page[^1].Id).Encode(ListScope) : null;
        return new SegmentListResponse(page, next);
    }

    public async Task<Result<SegmentResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Segments.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new
            {
                s.Id, s.Name, s.Description, s.Definition, s.Status, s.IsRealtimeEligible,
                s.MemberCount, s.LastRunAt, s.CurrentRunId, s.CreatedAt, s.UpdatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return SegmentErrors.NotFound;
        }

        return new SegmentResponse(
            row.Id, row.Name, row.Description, ToElement(row.Definition), row.Status.ToString(), row.IsRealtimeEligible,
            row.MemberCount,
            null, // realtimeMemberCount is always null in this plan (H2).
            UtcOrNull(row.LastRunAt), row.CurrentRunId, Utc(row.CreatedAt), Utc(row.UpdatedAt));
    }

    /// <summary>H10: keyset-paged by (CreatedAt, RunId) descending, tagged `segments-runs`, cursor scope
    /// `segment-runs`. Not found mirrors <see cref="GetAsync"/> — nothing about the segment's existence leaks.</summary>
    public async Task<Result<SegmentRunListResponse>> ListRunsAsync(Guid segmentId, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        if (!await db.Segments.AsNoTracking().AnyAsync(s => s.Id == segmentId, cancellationToken))
        {
            return SegmentErrors.NotFound;
        }

        var query = db.SegmentRuns.AsNoTracking().TagWith("segments-runs").Where(r => r.SegmentId == segmentId);

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(RunsListScope, cursor, out var after))
            {
                return PagingErrors.InvalidCursor;
            }

            var at = after.Timestamp;
            var id = after.Id;
            query = query.Where(r => r.CreatedAt <= at && (r.CreatedAt < at || r.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(size.Value + 1)
            .Select(r => new { r.Id, r.Status, r.Trigger, r.StartedAt, r.FinishedAt, r.MemberCount, r.Error, r.CreatedAt })
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(r => new SegmentRunListItem(r.Id, r.Status.ToString(), r.Trigger.ToString(), UtcOrNull(r.StartedAt), UtcOrNull(r.FinishedAt), r.MemberCount, r.Error, Utc(r.CreatedAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].CreatedAt, page[^1].RunId).Encode(RunsListScope) : null;
        return new SegmentRunListResponse(page, next);
    }

    /// <summary>H13: profiles of the segment's <c>CurrentRunId</c>, newest <c>ProfileId</c> first. The tenant and
    /// segment checks go through the EF-filtered <see cref="SegmentationDbContext"/> first (defence in depth: the
    /// raw SQL below also carries <c>m.TenantId = @tenant</c>, since it bypasses EF's global filter entirely) —
    /// only then is the raw SQL run, so an unknown or foreign id never reaches it.</summary>
    public async Task<Result<SegmentMemberListResponse>> ListMembersAsync(Guid segmentId, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        Guid? after = null;
        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(MembersScope, cursor, out var decoded))
            {
                return PagingErrors.InvalidCursor;
            }

            after = decoded.Id;
        }

        var segment = await db.Segments.AsNoTracking()
            .Where(s => s.Id == segmentId)
            .Select(s => new { s.CurrentRunId })
            .SingleOrDefaultAsync(cancellationToken);
        if (segment is null)
        {
            return SegmentErrors.NotFound;
        }

        if (segment.CurrentRunId is not { } runId)
        {
            // No run has ever finished for this segment: an empty page, not a 404 (task-8 brief).
            return new SegmentMemberListResponse([], null);
        }

        var take = new SqlParameter("@take", SqlDbType.Int) { Value = size.Value + 1 };
        var run = new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = runId };
        var tenant = new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = db.CurrentTenantId };

        // H13, measured: two constant statements, not one with "@after IS NULL OR …" (see MembersFirstPageSql's
        // doc comment) — each seeks PK_SegmentRunMembers on (RunId, ProfileId) directly.
        var rows = after is { } afterId
            ? await db.Database.SqlQueryRaw<MemberRow>(MembersCursorPageSql, take, run, tenant, new SqlParameter("@after", SqlDbType.UniqueIdentifier) { Value = afterId }).ToListAsync(cancellationToken)
            : await db.Database.SqlQueryRaw<MemberRow>(MembersFirstPageSql, take, run, tenant).ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(r => new SegmentMemberListItem(r.Id, r.ExternalId, r.Email, Utc(r.LastSeenAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(DateTime.UnixEpoch, page[^1].Id).Encode(MembersScope) : null;
        return new SegmentMemberListResponse(page, next);
    }

    /// <summary>IT-029: membership changes newest first (<c>OccurredAt</c>, <c>Id</c> descending), optionally
    /// filtered to <c>OccurredAt >= since</c>. Not found mirrors <see cref="GetAsync"/>.</summary>
    public async Task<Result<SegmentChangeListResponse>> ListChangesAsync(Guid segmentId, string? since, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        DateTime? sinceUtc = null;
        if (since is not null)
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return SegmentErrors.InvalidFilter("since", "must be a valid ISO-8601 timestamp");
            }

            sinceUtc = parsed.UtcDateTime;
        }

        if (!await db.Segments.AsNoTracking().AnyAsync(s => s.Id == segmentId, cancellationToken))
        {
            return SegmentErrors.NotFound;
        }

        var query = db.MembershipChanges.AsNoTracking().TagWith("segments-changes").Where(c => c.SegmentId == segmentId);

        if (sinceUtc is { } sinceAt)
        {
            query = query.Where(c => c.OccurredAt >= sinceAt);
        }

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(ChangesScope, cursor, out var after))
            {
                return PagingErrors.InvalidCursor;
            }

            var at = after.Timestamp;
            var id = after.Id;
            // Conjunctive form (ruling G3, mirrors EfProfileQueries): one contiguous range on OccurredAt.
            query = query.Where(c => c.OccurredAt <= at && (c.OccurredAt < at || c.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(c => c.OccurredAt).ThenByDescending(c => c.Id)
            .Take(size.Value + 1)
            .Select(c => new { c.Id, c.ProfileId, c.Change, c.Source, c.OccurredAt })
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(r => new SegmentChangeListItem(r.Id, r.ProfileId, r.Change.ToString(), r.Source.ToString(), Utc(r.OccurredAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].OccurredAt, page[^1].Id).Encode(ChangesScope) : null;
        return new SegmentChangeListResponse(page, next);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>The column carries an ISJSON check constraint, so the text always parses; Clone detaches it from the document.</summary>
    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary><see cref="MembersFirstPageSql"/>/<see cref="MembersCursorPageSql"/>'s projection.
    /// <see cref="LastSeenAt"/> comes back Kind=Unspecified — the SQL client does not know the column is UTC —
    /// so the caller re-tags it via <see cref="Utc"/>.</summary>
    private sealed record MemberRow(Guid Id, string? ExternalId, string? Email, DateTime LastSeenAt);
}
