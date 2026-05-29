using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Ordering;

// Retained for potential future use. BestMixOrderer no longer uses event clustering
// for ordering — see algorithm redesign notes in BestMixOrderer.cs (2026-05-26).
internal static class EventClusterer
{
    public static readonly TimeSpan EventGap = TimeSpan.FromMinutes(30);

    public static List<List<Photo>> Cluster(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) return new();

        var hasTimes = photos.Any(p => p.CaptureTime.HasValue);
        if (!hasTimes)
        {
            // No usable times → one bucket sorted by path (deterministic fallback)
            return new List<List<Photo>>
            {
                photos.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        var sorted = photos
            .OrderBy(p => p.CaptureTime ?? DateTime.MaxValue)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var clusters = new List<List<Photo>>();
        var current = new List<Photo> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var prevTime = sorted[i - 1].CaptureTime;
            var thisTime = sorted[i].CaptureTime;

            bool newCluster = prevTime.HasValue && thisTime.HasValue
                ? (thisTime.Value - prevTime.Value) > EventGap
                : false;

            if (newCluster)
            {
                clusters.Add(current);
                current = new List<Photo>();
            }
            current.Add(sorted[i]);
        }
        clusters.Add(current);
        return clusters;
    }
}
