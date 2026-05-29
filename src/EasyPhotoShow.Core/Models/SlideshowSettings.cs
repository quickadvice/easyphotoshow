namespace EasyPhotoShow.Core.Models;

public enum PhotoOrdering
{
    BestMix,
    KeepFolderOrder
}

public sealed class SlideshowSettings
{
    public required IReadOnlyList<Photo> Photos { get; init; }
    public required double SecondsPerPhoto { get; init; }
    public required PhotoOrdering Ordering { get; init; }
    public required TransitionStyle Transition { get; init; }
    public required MusicChoice Music { get; init; }
    public required string SlideshowName { get; init; }
    public required string SaveFolder { get; init; }

    // Optional bookend slides. Null = off. Pinned by RenderJob to the first/last
    // positions after ordering — never passed through BestMixOrderer.
    public SlideContent? OpenerSlide { get; init; }
    public SlideContent? CloserSlide { get; init; }

    public TimeSpan EstimatedRuntime =>
        TimeSpan.FromSeconds(Photos.Count * SecondsPerPhoto);

    public string OutputPath =>
        System.IO.Path.Combine(SaveFolder, SlideshowName + ".mp4");
}
