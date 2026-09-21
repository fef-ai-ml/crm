using Crm.Kernel;

namespace Crm.Segmentation.Domain.Segments;

/// <summary>segments.Segments: a saved rule tree plus the result of its most recent successful run.</summary>
public sealed class Segment : Entity
{
    /// <summary>Rule format version; 1 in slice 1 (docs/data-dictionary/segments.md).</summary>
    public const int CurrentDefinitionVersion = 1;

    private Segment()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Canonical JSON form of the rule tree (CanonicalRuleWriter.Write), spec §5.1.</summary>
    public string Definition { get; private set; } = string.Empty;

    public int DefinitionVersion { get; private set; } = CurrentDefinitionVersion;

    /// <summary>SHA-256 of Definition (DefinitionHash.Compute); shared with the preview cache key function.</summary>
    public byte[] DefinitionHash { get; private set; } = [];

    /// <summary>Result of the eligibility classifier at save time (RealtimeEligibility.Classify).</summary>
    public bool IsRealtimeEligible { get; private set; }

    public SegmentStatus Status { get; private set; }

    /// <summary>Null until the first successful run.</summary>
    public Guid? CurrentRunId { get; private set; }

    /// <summary>Copied from the current run; 0 until then.</summary>
    public int MemberCount { get; private set; }

    /// <summary>Finish time of the current run.</summary>
    public DateTime? LastRunAt { get; private set; }

    public static Segment Create(
        Guid tenantId,
        string name,
        string? description,
        string canonicalDefinition,
        byte[] definitionHash,
        bool isRealtimeEligible,
        SegmentStatus status)
        => new()
        {
            TenantId = tenantId,
            Name = name,
            Description = description,
            Definition = canonicalDefinition,
            DefinitionHash = definitionHash,
            IsRealtimeEligible = isRealtimeEligible,
            Status = status,
        };

    public void Update(string name, string? description, string canonicalDefinition, byte[] definitionHash, bool isRealtimeEligible)
    {
        Name = name;
        Description = description;
        Definition = canonicalDefinition;
        DefinitionHash = definitionHash;
        IsRealtimeEligible = isRealtimeEligible;
    }

    public void Archive() => Status = SegmentStatus.Archived;
}
