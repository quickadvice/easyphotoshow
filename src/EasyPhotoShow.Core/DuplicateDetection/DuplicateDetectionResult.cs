using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.DuplicateDetection;

// Wraps both the duplicate groups AND the dHash-by-path dictionary so that callers can
// re-attach dHashes to their working Photo list before passing photos downstream
// (Best Mix ordering, render pipeline). Without this, dHashes computed during detection
// are silently discarded — the visual-variety tiebreaker in Best Mix would always read
// DHash = null and no-op.
//
// ExactOnlyGroups and VisualGroups are filtered convenience views over Groups, partitioned
// by GroupClassification. Callers that want all groups (e.g. for legacy paths) can still
// use Groups; the two-track scan UX uses the filtered views.
public sealed class DuplicateDetectionResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }
    public required IReadOnlyDictionary<string, ulong> DHashByPath { get; init; }

    // Per-phase scan timings for the [TIMING-SCAN] diagnostic log. Detect populates the
    // Phase 2–5 fields; the caller fills the Index fields (it owns the FolderScanner.Scan
    // call). Always non-null so callers can write the log unconditionally.
    public ScanTimings Timings { get; init; } = new();

    public IReadOnlyList<DuplicateGroup> ExactOnlyGroups =>
        Groups.Where(g => g.Classification == GroupClassification.ExactOnly).ToList();

    public IReadOnlyList<DuplicateGroup> VisualGroups =>
        Groups.Where(g => g.Classification == GroupClassification.HasVisualComponent).ToList();
}
