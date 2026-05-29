using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Imaging;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Scanning;

namespace EasyPhotoShow.ScanDriver;

// Bug 2A diagnostic. Computes dHash for every photo in a folder, prints the full
// Hamming-distance matrix for "close" pairs, calls out specific named pairs the
// user asked about, then re-runs full duplicate-grouping at multiple thresholds.
//
// Does NOT mutate the source folder — never calls FolderScanner with any move logic,
// never invokes ExactGroupAutoResolver.
public static class ThresholdDiagnostic
{
    public static void Run(string folder)
    {
        Console.WriteLine($"=== ThresholdDiagnostic: {folder} ===");
        Console.WriteLine();

        var scanner = new FolderScanner();
        var scan = scanner.Scan(new[] { folder });
        Console.WriteLine($"Scanned {scan.Photos.Count} photos.");
        Console.WriteLine();

        // Compute dHash for every photo (parallel).
        var photos = scan.Photos.ToArray();
        var dHashes = new ulong?[photos.Length];
        Parallel.For(0, photos.Length, i =>
        {
            try { dHashes[i] = DHash.Compute(photos[i].Path); }
            catch { dHashes[i] = null; }
        });

        var photoNames = photos.Select(p => Path.GetFileName(p.Path)).ToArray();

        // Specific named-pair lookups requested by the user.
        Console.WriteLine("=== Named-pair lookups ===");
        PrintPair("All_Original.jpg", "Christmas2018.png", photoNames, dHashes);
        PrintPair("All_Original.png", "Christmas2018.png", photoNames, dHashes); // alt
        Console.WriteLine();

        // Building-themed candidates (manually filtered set) for the "abandoned building
        // interior" pair — print all pairwise distances so user can identify it.
        Console.WriteLine("=== Building-themed candidate pairs (for Pair A identification) ===");
        var buildingNames = new[] { "Ol_Red.jpg", "Ol_Scrap.jpg", "Old_Life.jpg",
            "Still_Standing.jpg", "Ross_Cabin.jpg", "Regal_Roost.jpg" };
        for (int i = 0; i < buildingNames.Length; i++)
            for (int j = i + 1; j < buildingNames.Length; j++)
                PrintPair(buildingNames[i], buildingNames[j], photoNames, dHashes);
        Console.WriteLine();

        // Print all pairs with Hamming distance <= 22, sorted ascending. Lets the user
        // eyeball other close-but-not-flagged pairs and identify pairs they expected.
        Console.WriteLine("=== All pairs with Hamming distance <= 22 (sorted ascending) ===");
        var allClosePairs = new List<(int dist, string a, string b)>();
        for (int i = 0; i < photos.Length; i++)
        {
            var hi = dHashes[i];
            if (hi is null) continue;
            for (int j = i + 1; j < photos.Length; j++)
            {
                var hj = dHashes[j];
                if (hj is null) continue;
                int d = (int)DHash.HammingDistance(hi.Value, hj.Value);
                if (d <= 22)
                    allClosePairs.Add((d, photoNames[i], photoNames[j]));
            }
        }
        foreach (var (d, a, b) in allClosePairs.OrderBy(x => x.dist).ThenBy(x => x.a))
            Console.WriteLine($"  dist={d,2}  {a}  <->  {b}");
        Console.WriteLine();

        // Run full classification at multiple thresholds. To do this without mutating
        // the global DHash.SimilarityThresholdBits, we re-implement the grouping logic
        // inline using a configurable threshold.
        Console.WriteLine("=== Group counts at varying thresholds ===");
        Console.WriteLine($"  (current DHash.SimilarityThresholdBits = {DHash.SimilarityThresholdBits})");
        var thresholds = new[] { 6, 8, 10, 12, 14, 16, 18, 20, 21, 22 };
        var groupsByThreshold = new Dictionary<int, List<List<string>>>();
        foreach (var t in thresholds)
        {
            var groups = GroupByThreshold(photos, dHashes, t);
            groupsByThreshold[t] = groups;
            Console.WriteLine($"  Threshold {t,2}: {groups.Count} groups, {groups.Sum(g => g.Count)} photos involved");
        }
        Console.WriteLine();

        // Diff: which groups appear at each threshold step that weren't present at the previous one?
        Console.WriteLine("=== Incremental NEW groups at each threshold ===");
        for (int idx = 1; idx < thresholds.Length; idx++)
        {
            var prev = thresholds[idx - 1];
            var curr = thresholds[idx];
            var prevHashed = new HashSet<string>(
                groupsByThreshold[prev].Select(g => string.Join("|", g.OrderBy(x => x))));
            var newOnes = groupsByThreshold[curr]
                .Where(g => !prevHashed.Contains(string.Join("|", g.OrderBy(x => x))))
                .ToList();
            if (newOnes.Count == 0) continue;
            Console.WriteLine($"  Threshold {curr} (vs {prev}):");
            foreach (var g in newOnes)
                Console.WriteLine($"    [{g.Count} photos] {string.Join(" + ", g)}");
        }
    }

    private static void PrintPair(string nameA, string nameB,
        string[] names, ulong?[] hashes)
    {
        int iA = Array.FindIndex(names, n => string.Equals(n, nameA, StringComparison.OrdinalIgnoreCase));
        int iB = Array.FindIndex(names, n => string.Equals(n, nameB, StringComparison.OrdinalIgnoreCase));
        if (iA < 0 || iB < 0)
        {
            // Skip silently if the file isn't present — these are best-effort lookups.
            return;
        }
        var hA = hashes[iA];
        var hB = hashes[iB];
        if (hA is null || hB is null)
        {
            Console.WriteLine($"  {nameA}  <->  {nameB}: decode failed for one or both");
            return;
        }
        int d = (int)DHash.HammingDistance(hA.Value, hB.Value);
        Console.WriteLine($"  {nameA}  <->  {nameB}: Hamming distance = {d}");
    }

    private static List<List<string>> GroupByThreshold(
        Photo[] photos, ulong?[] hashes, int threshold)
    {
        var uf = new UnionFindLocal(photos.Length);
        for (int i = 0; i < photos.Length; i++)
        {
            var hi = hashes[i];
            if (hi is null) continue;
            for (int j = i + 1; j < photos.Length; j++)
            {
                var hj = hashes[j];
                if (hj is null) continue;
                if (DHash.HammingDistance(hi.Value, hj.Value) <= threshold)
                    uf.Union(i, j);
            }
        }
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < photos.Length; i++)
        {
            int r = uf.Find(i);
            if (!groups.TryGetValue(r, out var list))
            {
                list = new List<int>();
                groups[r] = list;
            }
            list.Add(i);
        }
        return groups.Values
            .Where(g => g.Count >= 2)
            .Select(g => g.Select(i => Path.GetFileName(photos[i].Path)).ToList())
            .ToList();
    }

    private sealed class UnionFindLocal
    {
        private readonly int[] _parent;
        public UnionFindLocal(int n) { _parent = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; } return x; }
        public void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) _parent[ra] = rb; }
    }
}
