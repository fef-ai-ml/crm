using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Paging;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Segments;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Queries;

/// <summary>Read side over SegmentationDbContext. The tenant predicate is the context's global filter. The list
/// is keyset-paged by (CreatedAt, Id) descending (H10) and projects only the columns
/// <c>IX_Segments_TenantId_CreatedAt_Id</c> covers (H11), so the read never touches the base table.</summary>
internal sealed class EfSegmentQueries(SegmentationDbContext db) : ISegmentQueries
{
    private const string ListScope = "segments";

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
            return SegmentErrors.InvalidFilter("must be Draft, Active or Archived");
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

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? UtcOrNull(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>The column carries an ISJSON check constraint, so the text always parses; Clone detaches it from the document.</summary>
    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
