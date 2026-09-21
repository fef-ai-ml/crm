using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Segmentation.Application.Segments;
using Crm.Segmentation.Domain.Rules;
using Crm.Segmentation.Domain.Segments;
using Crm.Segmentation.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure.Segments;

/// <summary>Write side over SegmentationDbContext: parse → canonical → hash → classify → persist (H15, H20).
/// Field checks (name/description/status) run before the rule definition is parsed, so a request that fails
/// both reports <c>segments.invalid_request</c>, never <c>segments.invalid_definition</c> (cheap checks first,
/// docs/api/README.md).</summary>
internal sealed class EfSegmentService(SegmentationDbContext db, ITenantContext tenant) : ISegmentService
{
    private const int MaxNameLength = 128;
    private const int MaxDescriptionLength = 1024;

    public async Task<Result<SegmentResponse>> CreateAsync(SegmentWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = ValidateFields(request, statusRequired: false);
        if (fields.IsFailure)
        {
            return fields.Error!;
        }

        var (name, description, status) = fields.Value;

        var parsed = ParseDefinition(request.Definition);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var tree = parsed.Value;

        if (await db.Segments.AnyAsync(s => s.Name == name, cancellationToken))
        {
            return SegmentErrors.NameTaken;
        }

        var canonical = CanonicalRuleWriter.Write(tree);
        var hash = DefinitionHash.Compute(tree);
        var eligibility = RealtimeEligibility.Classify(tree);

        var segment = Segment.Create(tenant.TenantId, name, description, canonical, hash, eligibility.IsEligible, status ?? SegmentStatus.Draft);
        db.Segments.Add(segment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && SqlErrors.IsUniqueViolation(sql))
        {
            // Lost a race against a concurrent create of the same name; the pre-check above only catches the common case (H15).
            return SegmentErrors.NameTaken;
        }

        return ToResponse(segment);
    }

    public async Task<Result<SegmentResponse>> UpdateAsync(Guid id, SegmentWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = ValidateFields(request, statusRequired: true);
        if (fields.IsFailure)
        {
            return fields.Error!;
        }

        var (name, description, status) = fields.Value;

        var parsed = ParseDefinition(request.Definition);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var tree = parsed.Value;

        var segment = await db.Segments.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (segment is null)
        {
            return SegmentErrors.NotFound;
        }

        if (!string.Equals(segment.Name, name, StringComparison.Ordinal)
            && await db.Segments.AnyAsync(s => s.Id != id && s.Name == name, cancellationToken))
        {
            return SegmentErrors.NameTaken;
        }

        var canonical = CanonicalRuleWriter.Write(tree);
        var hash = DefinitionHash.Compute(tree);
        var eligibility = RealtimeEligibility.Classify(tree);
        segment.Update(name, description, canonical, hash, eligibility.IsEligible, status!.Value);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sql && SqlErrors.IsUniqueViolation(sql))
        {
            return SegmentErrors.NameTaken;
        }

        return ToResponse(segment);
    }

    public async Task<Result> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var segment = await db.Segments.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (segment is null)
        {
            return Result.Fail(SegmentErrors.NotFound);
        }

        if (segment.Status != SegmentStatus.Archived)
        {
            segment.Archive();
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Ok();
    }

    private static Result<RuleNode> ParseDefinition(JsonElement definition)
    {
        var parsed = RuleParser.Parse(definition);
        return parsed.IsValid ? parsed.Tree! : SegmentErrors.InvalidDefinition(parsed.Errors);
    }

    private static Result<(string Name, string? Description, SegmentStatus? Status)> ValidateFields(SegmentWriteRequest request, bool statusRequired)
    {
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            fields["name"] = [$"must be 1 to {MaxNameLength} characters after trimming"];
        }

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            fields["description"] = [$"must be at most {MaxDescriptionLength} characters"];
        }

        SegmentStatus? status = null;
        if (request.Status is null)
        {
            if (statusRequired)
            {
                fields["status"] = ["is required"];
            }
        }
        else if (Enum.TryParse<SegmentStatus>(request.Status, ignoreCase: true, out var parsedStatus))
        {
            status = parsedStatus;
        }
        else
        {
            fields["status"] = ["must be Draft, Active or Archived"];
        }

        return fields.Count > 0
            ? SegmentErrors.InvalidRequest(fields)
            : (name!, request.Description, status);
    }

    private static SegmentResponse ToResponse(Segment segment) => new(
        segment.Id,
        segment.Name,
        segment.Description,
        ToElement(segment.Definition),
        segment.Status.ToString(),
        segment.IsRealtimeEligible,
        segment.MemberCount,
        null, // realtimeMemberCount is always null in this plan (H2); it arrives with the real-time plan.
        segment.LastRunAt is { } lastRunAt ? DateTime.SpecifyKind(lastRunAt, DateTimeKind.Utc) : null,
        segment.CurrentRunId,
        DateTime.SpecifyKind(segment.CreatedAt, DateTimeKind.Utc),
        DateTime.SpecifyKind(segment.UpdatedAt, DateTimeKind.Utc));

    /// <summary>The column carries an ISJSON check constraint, so the text always parses; Clone detaches it from the document.</summary>
    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
