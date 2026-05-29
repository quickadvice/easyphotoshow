using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.App.Session;

// Session-scoped state — V1 is explicitly session-based (no project save). All in-memory.
public sealed class SlideshowSession
{
    public List<string> SourceFolders { get; } = new();
    public ScanResult? Scan { get; set; }
    public IReadOnlyList<DuplicateGroup>? DuplicateGroups { get; set; }
    public IReadOnlyList<Photo>? PhotosForSlideshow { get; set; }
    public SlideshowSettings? Settings { get; set; }
    public string? FinishedOutputPath { get; set; }

    // Optional opening / closing slide content chosen on the Slideshow Settings screen.
    // Null = off. Session-only, consistent with the rest of this state.
    public SlideContent? OpenerSlide { get; set; }
    public SlideContent? CloserSlide { get; set; }

    // Number of duplicate FILES (not groups) auto-resolved during scan.
    // Set by ScanningViewModel after Phase 2 + auto-resolve; read by
    // DuplicateReviewViewModel to render the "N exact duplicate files were also
    // set aside safely" line in the review header.
    public int ExactCopyCount { get; set; }
}
