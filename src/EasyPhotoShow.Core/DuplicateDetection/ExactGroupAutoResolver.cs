using EasyPhotoShow.Core.Files;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.DuplicateDetection;

// Pure helper that takes the list of ExactOnly groups produced by DuplicateDetector and:
//   1. Identifies the recommended photo in each group via RecommendedPhotoSelector
//   2. Moves every non-recommended photo to its PotentialDuplicates folder
//   3. Returns the updated PhotosForSlideshow list (original minus successfully-moved paths)
// plus a MoveReport surfacing any failures.
//
// Extracted from ScanningViewModel so it can be unit-tested without WPF / App.Session
// state. The view model orchestrates calls to this helper, then writes the result to
// App.Session.PhotosForSlideshow before the completion beat.
public static class ExactGroupAutoResolver
{
    public sealed class Result
    {
        public required IReadOnlyList<Photo> RemainingPhotos { get; init; }
        public required int GroupsResolved { get; init; }
        public required int PhotosMoved { get; init; }
        public required PotentialDuplicatesMover.MoveReport MoveReport { get; init; }
    }

    public static Result Resolve(
        IReadOnlyList<DuplicateGroup> exactOnlyGroups,
        IReadOnlyList<Photo> currentPhotosForSlideshow,
        IEnumerable<string> sourceFolders,
        CancellationToken ct = default)
    {
        var mover = new PotentialDuplicatesMover();
        var report = new PotentialDuplicatesMover.MoveReport();

        if (exactOnlyGroups.Count == 0)
        {
            return new Result
            {
                RemainingPhotos = currentPhotosForSlideshow,
                GroupsResolved = 0,
                PhotosMoved = 0,
                MoveReport = report
            };
        }

        var toMove = new List<Photo>();
        foreach (var group in exactOnlyGroups)
        {
            ct.ThrowIfCancellationRequested();
            var recommended = RecommendedPhotoSelector.Pick(group.Photos);
            foreach (var photo in group.Photos)
            {
                if (!string.Equals(photo.Path, recommended.Path, StringComparison.OrdinalIgnoreCase))
                    toMove.Add(photo);
            }
        }

        if (toMove.Count == 0)
        {
            return new Result
            {
                RemainingPhotos = currentPhotosForSlideshow,
                GroupsResolved = exactOnlyGroups.Count,
                PhotosMoved = 0,
                MoveReport = report
            };
        }

        report = mover.Move(toMove, sourceFolders, ct);

        // Only photos that successfully moved should be removed from the slideshow list.
        // Failed moves leave the photo in place on disk and in the slideshow — acceptable
        // fallback per spec ("never a crash"). Compare by case-insensitive path.
        var movedPaths = new HashSet<string>(
            report.Moved.Select(p => p.Path),
            StringComparer.OrdinalIgnoreCase);
        var remaining = currentPhotosForSlideshow
            .Where(p => !movedPaths.Contains(p.Path))
            .ToList();

        return new Result
        {
            RemainingPhotos = remaining,
            GroupsResolved = exactOnlyGroups.Count,
            PhotosMoved = report.Moved.Count,
            MoveReport = report
        };
    }
}
