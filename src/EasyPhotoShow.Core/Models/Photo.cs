namespace EasyPhotoShow.Core.Models;

public sealed record Photo
{
    public required string Path { get; init; }
    public required long FileSize { get; init; }
    public required PhotoFormat Format { get; init; }
    public required int VisualWidth { get; init; }
    public required int VisualHeight { get; init; }
    public DateTime? CaptureTime { get; init; }
    public ulong? DHash { get; init; }
    public string? Sha256 { get; init; }
}
