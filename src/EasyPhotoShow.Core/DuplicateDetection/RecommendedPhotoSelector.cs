using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.DuplicateDetection;

internal static class RecommendedPhotoSelector
{
    // Highest resolution → largest file size → earliest capture time → shortest path
    public static Photo Pick(IEnumerable<Photo> candidates)
    {
        return candidates
            .OrderByDescending(p => (long)p.VisualWidth * p.VisualHeight)
            .ThenByDescending(p => p.FileSize)
            .ThenBy(p => p.CaptureTime ?? DateTime.MaxValue)
            .ThenBy(p => p.Path.Length)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
