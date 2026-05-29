namespace EasyPhotoShow.Core.Models;

public sealed class NormalizedBitmap
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] PixelsRgba { get; init; }
}
