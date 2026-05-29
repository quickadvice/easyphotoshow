using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Scanning;
using EasyPhotoShow.ScanDriver;

if (args.Length > 0 && args[0] == "--threshold-diagnostic")
{
    ThresholdDiagnostic.Run(args.Length > 1 ? args[1] : @"D:\Test_1");
    return;
}

if (args.Length > 0 && args[0] == "--bestmix-diagnostic")
{
    BestMixDiagnostic.Run(args.Length > 1 ? args[1] : @"D:\Test_1");
    return;
}

// Replays what ScanningViewModel does to the engine, but with no UI:
//   1. FolderScanner.Scan
//   2. DuplicateDetector.Detect (with onExactGroupsReady streaming callback)
//   3. ExactGroupAutoResolver.Resolve
// Then prints what happened so we can compare against expectations and
// inspect the filesystem outcome by eye.

string folder = args.Length > 0 ? args[0] : @"D:\Test_1";
Console.WriteLine($"=== Scanning {folder} ===");

var scanner = new FolderScanner();
var scan = scanner.Scan(new[] { folder });
Console.WriteLine($"Phase 1: {scan.Photos.Count} photos, {scan.UnsupportedFileCount} unsupported");

var detector = new DuplicateDetector();
int streamedGroups = 0;
int streamedDupes = 0;
Action<IReadOnlyList<DuplicateGroup>> onExactReady = groups =>
{
    streamedGroups += groups.Count;
    streamedDupes += groups.Sum(g => g.Photos.Count - 1);
    Console.WriteLine($"[stream] Phase 2 emitted {groups.Count} ExactOnly group(s); cumulative {streamedGroups} groups / {streamedDupes} dup files");
    foreach (var g in groups)
    {
        Console.WriteLine($"  group ({g.Photos.Count} photos, classification={g.Classification}):");
        foreach (var p in g.Photos)
            Console.WriteLine($"    {(ReferenceEquals(p, g.Recommended) ? "KEEP" : "move")}: {Path.GetFileName(p.Path)}");
    }
};

var result = detector.Detect(scan.Photos, progress: null, onExactGroupsReady: onExactReady);
Console.WriteLine();
Console.WriteLine($"Phase 3 done. Final classification:");
Console.WriteLine($"  Total groups: {result.Groups.Count}");
Console.WriteLine($"  ExactOnly groups: {result.ExactOnlyGroups.Count}");
Console.WriteLine($"  HasVisualComponent groups: {result.VisualGroups.Count}");
Console.WriteLine();

Console.WriteLine("=== ExactOnly groups (auto-resolve targets) ===");
foreach (var g in result.ExactOnlyGroups)
{
    Console.WriteLine($"  group ({g.Photos.Count} photos):");
    foreach (var p in g.Photos)
    {
        var marker = string.Equals(p.Path, g.Recommended.Path, StringComparison.OrdinalIgnoreCase) ? "KEEP" : "move";
        Console.WriteLine($"    {marker}: {Path.GetFileName(p.Path)} ({p.VisualWidth}x{p.VisualHeight}, {p.FileSize:N0} bytes)");
    }
}

Console.WriteLine();
Console.WriteLine("=== Visual groups (would go to review screen) ===");
foreach (var g in result.VisualGroups)
{
    Console.WriteLine($"  group ({g.Photos.Count} photos):");
    foreach (var p in g.Photos)
    {
        var marker = string.Equals(p.Path, g.Recommended.Path, StringComparison.OrdinalIgnoreCase) ? "[rec]" : "     ";
        Console.WriteLine($"    {marker} {Path.GetFileName(p.Path)} ({p.VisualWidth}x{p.VisualHeight})");
    }
}

Console.WriteLine();
Console.WriteLine("=== Running ExactGroupAutoResolver ===");
var resolve = ExactGroupAutoResolver.Resolve(
    result.ExactOnlyGroups,
    scan.Photos,
    new[] { folder });

Console.WriteLine($"GroupsResolved: {resolve.GroupsResolved}");
Console.WriteLine($"PhotosMoved:    {resolve.PhotosMoved}");
Console.WriteLine($"Moved files:");
foreach (var p in resolve.MoveReport.Moved)
    Console.WriteLine($"  - {Path.GetFileName(p.Path)}");
if (resolve.MoveReport.Failed.Count > 0)
{
    Console.WriteLine($"Failed:");
    foreach (var (p, reason) in resolve.MoveReport.Failed)
        Console.WriteLine($"  - {Path.GetFileName(p.Path)}: {reason}");
}
Console.WriteLine($"RemainingPhotos count: {resolve.RemainingPhotos.Count}");
Console.WriteLine();
Console.WriteLine($"Stream-vs-final reconciliation:");
Console.WriteLine($"  Phase-2 streamed groups: {streamedGroups}  (optimistic UI counter)");
Console.WriteLine($"  Final ExactOnly groups:  {result.ExactOnlyGroups.Count}  (post Phase-3 reclassification)");
Console.WriteLine($"  Streamed dup files:      {streamedDupes}");
Console.WriteLine($"  Actually moved:          {resolve.PhotosMoved}");
