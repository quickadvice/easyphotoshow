using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Imaging;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Ordering;
using EasyPhotoShow.Core.Scanning;

namespace EasyPhotoShow.ScanDriver;

// Drives the new spacing-based BestMixOrderer end-to-end against a real folder:
//   1. FolderScanner indexes the folder
//   2. DuplicateDetector.Detect computes dHash values (and SHA256 for size-collision photos)
//   3. AttachDHashes re-attaches dHash to the working Photo list
//   4. BestMixOrderer.Order produces the final slideshow sequence
// Then prints the ordered sequence plus a similarity-spacing report so we can verify
// that visually similar photos really are distributed and not bunched into sections.
//
// Read-only: never mutates the source folder, never invokes ExactGroupAutoResolver.
public static class BestMixDiagnostic
{
    // Mirror BestMixOrderer's similarity criteria for the report-side cluster rebuild.
    // Kept private and local so changes here can't drift the production algorithm.
    private static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(30);

    public static void Run(string folder)
    {
        Console.WriteLine($"=== BestMixDiagnostic: {folder} ===");
        Console.WriteLine();

        var scanner = new FolderScanner();
        var scan = scanner.Scan(new[] { folder });
        Console.WriteLine($"Scanned {scan.Photos.Count} photos ({scan.UnsupportedFileCount} unsupported skipped).");

        // Run the detector to harvest dHash values, then attach them to the working photo
        // list — the exact data flow ScanningViewModel uses before invoking BestMixOrderer.
        var detector = new DuplicateDetector();
        var result = detector.Detect(scan.Photos);
        var photos = DuplicateDetector.AttachDHashes(scan.Photos, result.DHashByPath);
        Console.WriteLine($"dHash values computed: {result.DHashByPath.Count} / {photos.Count}.");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ordered = BestMixOrderer.Order(photos);
        sw.Stop();
        Console.WriteLine($"BestMixOrderer.Order completed in {sw.ElapsedMilliseconds} ms.");
        Console.WriteLine();

        // ── Sequence print ─────────────────────────────────────────────────────────
        // [slot] filename | dHash_hex | captureTime
        Console.WriteLine("=== Ordered sequence ===");
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            string dhash = p.DHash.HasValue ? p.DHash.Value.ToString("x16") : "----------------";
            string capture = p.CaptureTime.HasValue
                ? p.CaptureTime.Value.ToString("yyyy-MM-dd HH:mm")
                : "----------------";
            Console.WriteLine($"[{i + 1:0000}] {Path.GetFileName(p.Path),-50} | {dhash} | {capture}");
        }
        Console.WriteLine();

        // ── Similarity report ──────────────────────────────────────────────────────
        // Rebuild similarity clusters using the SAME criteria the orderer uses (dHash
        // ≤ threshold OR capture-time within session window), then look up where each
        // cluster member landed in the ordered output and report spacing stats.
        var slotByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ordered.Count; i++) slotByPath[ordered[i].Path] = i;

        int n = photos.Count;
        var uf = new UnionFindLocal(n);
        for (int i = 0; i < n; i++)
        {
            var pi = photos[i];
            for (int j = i + 1; j < n; j++)
            {
                var pj = photos[j];
                bool dHashClose = pi.DHash.HasValue && pj.DHash.HasValue
                    && DHash.HammingDistance(pi.DHash.Value, pj.DHash.Value) <= DHash.SimilarityThresholdBits;
                bool timeClose = pi.CaptureTime.HasValue && pj.CaptureTime.HasValue
                    && AbsDelta(pi.CaptureTime.Value, pj.CaptureTime.Value) <= SessionWindow;
                if (dHashClose || timeClose) uf.Union(i, j);
            }
        }

        var groupsByRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = uf.Find(i);
            if (!groupsByRoot.TryGetValue(r, out var list))
            {
                list = new List<int>();
                groupsByRoot[r] = list;
            }
            list.Add(i);
        }

        var multiPhotoClusters = groupsByRoot.Values
            .Where(idxList => idxList.Count >= 2)
            .Select(idxList => idxList
                .Select(idx => photos[idx])
                .OrderBy(p => slotByPath[p.Path])
                .ToList())
            .OrderByDescending(list => list.Count)
            .ThenBy(list => slotByPath[list[0].Path])
            .ToList();

        Console.WriteLine("=== Similarity spacing report ===");
        Console.WriteLine($"Groups of similar photos (dHash distance ≤ {DHash.SimilarityThresholdBits} OR capture time within 30 min):");
        Console.WriteLine($"Multi-photo clusters: {multiPhotoClusters.Count} (singletons omitted).");
        Console.WriteLine();

        int violationThreshold = 10;
        var violations = new List<string>();

        for (int g = 0; g < multiPhotoClusters.Count; g++)
        {
            var cluster = multiPhotoClusters[g];
            var slots = cluster.Select(p => slotByPath[p.Path]).ToList(); // already sorted ascending
            var gaps = new List<int>();
            for (int k = 1; k < slots.Count; k++) gaps.Add(slots[k] - slots[k - 1]);

            string slotList = slots.Count <= 12
                ? string.Join(", ", slots)
                : string.Join(", ", slots.Take(8)) + $", ..., {slots[^1]} ({slots.Count} total)";

            Console.WriteLine($"Group {g + 1} ({cluster.Count} photos): slots {slotList}");

            if (gaps.Count > 0)
            {
                Console.WriteLine($"  Min spacing: {gaps.Min()} slots");
                Console.WriteLine($"  Max spacing: {gaps.Max()} slots");
                Console.WriteLine($"  Average spacing: {gaps.Average():F1} slots");
            }

            // Sample file names so the user can sanity-check what "cluster" means here.
            var sampleNames = cluster.Take(3).Select(p => Path.GetFileName(p.Path));
            Console.WriteLine($"  Sample files: {string.Join(", ", sampleNames)}" +
                              (cluster.Count > 3 ? $" (+{cluster.Count - 3} more)" : ""));

            // Record any pairwise spacing under the violation threshold.
            for (int k = 0; k < gaps.Count; k++)
            {
                if (gaps[k] < violationThreshold)
                    violations.Add(
                        $"Group {g + 1}: photos at slots {slots[k]} and {slots[k + 1]} are only {gaps[k]} apart " +
                        $"({Path.GetFileName(cluster[k].Path)} ↔ {Path.GetFileName(cluster[k + 1].Path)})");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"=== Any spacing violations (< {violationThreshold} slots) ===");
        if (violations.Count == 0)
            Console.WriteLine("None found.");
        else
            foreach (var v in violations) Console.WriteLine(v);
    }

    private static TimeSpan AbsDelta(DateTime a, DateTime b) => a > b ? a - b : b - a;

    // Local copy — production UnionFind is internal to Core and inaccessible from the
    // ScanDriver assembly. Mirrors the same path-compression behavior; only Find/Union
    // are needed for the report.
    private sealed class UnionFindLocal
    {
        private readonly int[] _parent;
        public UnionFindLocal(int n) { _parent = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; } return x; }
        public void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) _parent[ra] = rb; }
    }
}
