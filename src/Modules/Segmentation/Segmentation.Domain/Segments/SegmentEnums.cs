namespace Crm.Segmentation.Domain.Segments;

/// <summary>segments.Segments.Status (varchar(16), CK_Segments_Status).</summary>
public enum SegmentStatus { Draft, Active, Archived }

/// <summary>segments.SegmentRuns.Status (varchar(16), CK_SegmentRuns_Status).</summary>
public enum SegmentRunStatus { Queued, Running, Succeeded, Failed }

/// <summary>segments.SegmentRuns.Trigger (varchar(16), CK_SegmentRuns_Trigger): who queued the run.</summary>
public enum SegmentRunTrigger { Manual, Schedule }

/// <summary>segments.MembershipChanges.Change (varchar(8), CK_MembershipChanges_Change).</summary>
public enum MembershipChangeKind { Entered, Exited }

/// <summary>segments.MembershipChanges.Source (varchar(8), CK_MembershipChanges_Source): which path recorded the change.</summary>
public enum MembershipChangeSource { Batch, Realtime }
