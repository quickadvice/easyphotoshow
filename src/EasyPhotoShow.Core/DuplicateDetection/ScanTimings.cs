namespace EasyPhotoShow.Core.DuplicateDetection;

// Per-phase wall-clock timings for the duplicate-detection scan, captured for the
// [TIMING-SCAN] diagnostic log (see ScanningViewModel.WriteScanTimingLog). Mirrors the
// render pipeline's [timing]/[TIMING-S1] instrumentation so large-batch behavior
// (3,000–4,000 photos) can be diagnosed phase by phase — the O(n²) Pairwise phase is the
// one to watch at scale.
//
// DuplicateDetector.Detect populates ExactMatch through GroupBuild (Phases 2–5). The
// Index phase runs in FolderScanner.Scan, BEFORE Detect is called, so the caller
// (ScanningViewModel) measures it and fills Index/PhotosScanned/UnsupportedCount on the
// returned instance. Mutable class rather than a readonly struct precisely so the caller
// can set those three fields without rebuilding the object.
public sealed class ScanTimings
{
    // Phase 1 — FolderScanner.Scan(): filesystem walk + EXIF read. Set by the caller.
    public TimeSpan Index { get; set; }
    public int PhotosScanned { get; set; }
    public int UnsupportedCount { get; set; }

    // Phase 2 — SHA-256 on size-collision files only.
    public TimeSpan ExactMatch { get; set; }
    public int FilesHashed { get; set; }

    // Phase 3 — Magick.NET decode + dHash compute (runs on every indexed photo).
    public TimeSpan DHashCompute { get; set; }
    public int PhotosHashed { get; set; }

    // Phase 4 — O(n²) Hamming-distance comparisons. PairsCompared = n × (n-1) / 2.
    public TimeSpan Pairwise { get; set; }
    public long PairsCompared { get; set; }

    // Phase 5 — union-find duplicate-group formation. GroupsFound = final duplicate groups.
    public TimeSpan GroupBuild { get; set; }
    public int GroupsFound { get; set; }

    public TimeSpan Total => Index + ExactMatch + DHashCompute + Pairwise + GroupBuild;
}
