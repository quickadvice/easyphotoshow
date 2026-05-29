namespace EasyPhotoShow.Core.Models;

public sealed class ScanResult
{
    public required IReadOnlyList<Photo> Photos { get; init; }
    public required int UnsupportedFileCount { get; init; }
}
