namespace EasyPhotoShow.Core.Models;

public enum RenderStage
{
    PreparingPhotos,
    CreatingSlideshow,
    AddingMusic,
    FinalizingSlideshow,
    Complete
}

public sealed class RenderProgress
{
    public required RenderStage Stage { get; init; }
    public required double FractionComplete { get; init; }
    public required int PhotosProcessed { get; init; }
    public required int PhotosTotal { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}
