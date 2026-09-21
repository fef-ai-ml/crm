using System.Text.Json;

namespace Crm.Segmentation.Application.Segments;

/// <summary>Body of POST /segments and PUT /segments/{id} (task 5 brief, JSON camelCase). <see cref="Definition"/>
/// is a raw <see cref="JsonElement"/> so <c>RuleParser</c> validates it; a missing or JSON-null "definition"
/// property deserializes to <c>default(JsonElement)</c> (ValueKind Undefined), which the rule parser itself
/// rejects at path "definition" — no separate null check is needed.</summary>
public sealed record SegmentWriteRequest(string? Name, string? Description, JsonElement Definition, string? Status);

/// <summary>Row of GET /segments — the columns covered by <c>IX_Segments_TenantId_CreatedAt_Id</c> (H11) plus
/// <c>Id</c>/<c>CreatedAt</c>; no <c>description</c> or <c>definition</c> here (detail-only, ruling H2/F7 idiom).</summary>
public sealed record SegmentListItem(Guid Id, string Name, string Status, int MemberCount, DateTime? LastRunAt, bool IsRealtimeEligible, DateTime CreatedAt);

public sealed record SegmentListResponse(IReadOnlyList<SegmentListItem> Items, string? NextCursor);

/// <summary><see cref="Definition"/> is always the canonical form (<c>CanonicalRuleWriter.Write</c>).
/// <see cref="RealtimeMemberCount"/> is always null in this plan (H2); it arrives with the real-time plan.</summary>
public sealed record SegmentResponse(
    Guid Id, string Name, string? Description, JsonElement Definition, string Status, bool IsRealtimeEligible,
    int MemberCount, int? RealtimeMemberCount, DateTime? LastRunAt, Guid? CurrentRunId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Body of POST /segments/preview (task-6 brief): the same raw-<see cref="JsonElement"/> definition as
/// <see cref="SegmentWriteRequest"/>, with no name, description or status — a preview never persists anything.</summary>
public sealed record PreviewRequest(JsonElement Definition);

/// <summary><see cref="Count"/> and <see cref="CachedAt"/> are null exactly when <see cref="TimedOut"/> is true —
/// the five-second command budget elapsed without a countable answer, and nothing was cached (task-6 brief).</summary>
public sealed record PreviewResponse(int? Count, bool TimedOut, DateTime? CachedAt);

/// <summary>Body of the 202 response to POST /segments/{id}/run (task-7 brief, H5): the existing run's id when a
/// Queued or Running run for the segment already exists, otherwise the newly queued run's id.</summary>
public sealed record RunQueuedResponse(Guid RunId);

/// <summary>Row of GET /segments/{id}/runs, newest first (H10).</summary>
public sealed record SegmentRunListItem(
    Guid RunId, string Status, string Trigger, DateTime? StartedAt, DateTime? FinishedAt, int? MemberCount, string? Error, DateTime CreatedAt);

public sealed record SegmentRunListResponse(IReadOnlyList<SegmentRunListItem> Items, string? NextCursor);

/// <summary>Row of GET /segments/{id}/members (task-8 brief, H13): a profile in the segment's current run, joined
/// to `profiles.Profiles` by raw SQL. <see cref="Id"/> is the profile id.</summary>
public sealed record SegmentMemberListItem(Guid Id, string? ExternalId, string? Email, DateTime LastSeenAt);

public sealed record SegmentMemberListResponse(IReadOnlyList<SegmentMemberListItem> Items, string? NextCursor);

/// <summary>Row of GET /segments/{id}/changes (task-8 brief, IT-029), newest first. <see cref="Id"/> is the
/// `MembershipChanges` row's own id — the cursor's tiebreaker, not a profile id.</summary>
public sealed record SegmentChangeListItem(Guid Id, Guid ProfileId, string Change, string Source, DateTime OccurredAt);

public sealed record SegmentChangeListResponse(IReadOnlyList<SegmentChangeListItem> Items, string? NextCursor);
