namespace Crm.Segmentation.Application;

/// <summary>Which compiled statement <see cref="ISegmentationHooks.RewriteQuery"/> is rewriting (task-6 brief).
/// <see cref="Run"/> and <see cref="Members"/> are wired by later tasks; defined together now so the interface
/// does not change shape underneath them.</summary>
public enum SegmentQueryKind { Preview, Run, Members }
