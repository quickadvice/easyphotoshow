using System.Diagnostics;
using System.Security.Cryptography;
using EasyPhotoShow.Core.Imaging;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.DuplicateDetection;

public sealed class DuplicateDetector
{
    public sealed class Progress
    {
        public int PhotosProcessed { get; set; }
        public int PhotosTotal { get; set; }
        public string Stage { get; set; } = "";
    }

    public DuplicateDetectionResult Detect(
        IReadOnlyList<Photo> photos,
        IProgress<Progress>? progress = null,
        Action<IReadOnlyList<DuplicateGroup>>? onExactGroupsReady = null,
        CancellationToken ct = default)
    {
        // Caller fills Index/PhotosScanned/UnsupportedCount; Detect owns Phases 2–5.
        var timings = new ScanTimings();

        if (photos.Count < 2)
        {
            // Nothing to compare: Phase 2–5 timings stay zero, which is accurate.
            timings.PhotosHashed = photos.Count;
            return new DuplicateDetectionResult
            {
                Groups = Array.Empty<DuplicateGroup>(),
                DHashByPath = new Dictionary<string, ulong>(),
                Timings = timings
            };
        }

        // Phase 2 — exact match (only files with size collisions get hashed). Pair tracking
        // separates exact relationships from perceptual ones so the final groups can be
        // classified ExactOnly vs HasVisualComponent for the two-track scan UX.
        var exactPairs = new HashSet<(int, int)>();
        var exactSw = Stopwatch.StartNew();
        var hashed = ComputeShaHashesForSizeMatches(photos, progress, ct);
        exactSw.Stop();
        timings.ExactMatch = exactSw.Elapsed;
        timings.FilesHashed = hashed.Count(p => p.Sha256 is not null);

        // Build provisional exact-only groups right after Phase 2 completes — these are
        // STABLE (byte-identical matches never get reorganized by Phase 3) and can be
        // surfaced to the scan screen immediately for auto-resolution. Phase 3's perceptual
        // pass may later add MORE relationships that bridge these groups; if that happens,
        // the bridged groups get reclassified as HasVisualComponent in the final result and
        // the auto-resolution will be re-evaluated by the caller.
        if (onExactGroupsReady is not null)
        {
            var provisional = BuildProvisionalExactGroups(hashed, exactPairs);
            onExactGroupsReady(provisional);
        }
        else
        {
            // Still need to populate exactPairs so the final classification is correct
            PopulateExactPairs(hashed, exactPairs);
        }

        // Phase 3 — perceptual: compute dHash for every photo
        var dhashSw = Stopwatch.StartNew();
        var withHashes = ComputeDHashes(hashed, progress, ct);
        dhashSw.Stop();
        timings.DHashCompute = dhashSw.Elapsed;
        timings.PhotosHashed = withHashes.Count;

        // Harvest dHashes by path so callers can re-attach them to their working
        // photo list. Without this step, the dHashes are lost when Detect returns
        // and BestMixOrderer's variety tiebreaker silently no-ops.
        var dHashByPath = new Dictionary<string, ulong>(
            capacity: withHashes.Count,
            comparer: StringComparer.OrdinalIgnoreCase);
        foreach (var p in withHashes)
        {
            if (p.DHash is ulong h)
                dHashByPath[p.Path] = h;
        }

        // Surface a "Grouping" label so the UI bridges the gap between the last
        // per-photo Phase-3 update and BuildGroups completing. On small collections
        // BuildGroups is microseconds; on 1,000+ photo collections it's measurable
        // because of the O(n²) pairwise dHash comparison. Either way the label
        // gives the user a clear "still working" signal during this phase.
        progress?.Report(new Progress { Stage = "Grouping possible duplicates" });

        // Group via union-find across exact + perceptual relationships. visualPairs
        // captures Phase-3-only relationships separately for classification below.
        // BuildGroups records Phase 4 (pairwise) and Phase 5 (union-find) timings.
        var visualPairs = new HashSet<(int, int)>();
        var groups = BuildGroups(withHashes, exactPairs, visualPairs, timings);

        // Phase 4 compares every pair exactly once: n × (n-1) / 2 (n = indexed photos).
        long n = withHashes.Count;
        timings.PairsCompared = n * (n - 1) / 2;

        var duplicateGroups = groups
            .Where(g => g.Indices.Count >= 2)
            .Select(g => new DuplicateGroup
            {
                Photos = g.Indices.Select(i => withHashes[i]).ToList(),
                Recommended = RecommendedPhotoSelector.Pick(g.Indices.Select(i => withHashes[i])),
                Classification = ClassifyGroup(g.Indices, exactPairs, visualPairs)
            })
            .ToList();

        // Post-process: when a HasVisualComponent group contains byte-identical sub-clusters
        // (e.g. A=B exact + C visually similar to A → union-find merges {A,B,C} as visual),
        // extract those sub-clusters as standalone ExactOnly groups so the auto-resolver
        // handles them, and reduce the visual group to [winner_per_subcluster + singletons].
        // Without this, byte-identical files leak onto the review screen — the user reported
        // this as a bug: photos that should have been silently auto-resolved appear on review.
        duplicateGroups = ExtractExactSubclustersFromVisualGroups(duplicateGroups);

        var ordered = duplicateGroups
            .OrderByDescending(g => g.Photos.Count)
            .ThenBy(g => g.Recommended.CaptureTime ?? DateTime.MaxValue)
            .ToList();

        timings.GroupsFound = ordered.Count;

        return new DuplicateDetectionResult
        {
            Groups = ordered,
            DHashByPath = dHashByPath,
            Timings = timings
        };
    }

    // Helper for callers that have an existing Photo list and want to attach the
    // computed dHashes back to it (so downstream consumers see DHash != null).
    public static IReadOnlyList<Photo> AttachDHashes(
        IReadOnlyList<Photo> photos,
        IReadOnlyDictionary<string, ulong> dHashByPath)
    {
        if (dHashByPath.Count == 0) return photos;
        var result = new Photo[photos.Count];
        for (int i = 0; i < photos.Count; i++)
        {
            var p = photos[i];
            result[i] = p.DHash is null && dHashByPath.TryGetValue(p.Path, out var h)
                ? p with { DHash = h }
                : p;
        }
        return result;
    }

    // Runs UnionFind on exactPairs ONLY (no dHash data yet), produces groups that are
    // guaranteed to be byte-identical-match clusters. These are safe to auto-resolve
    // without user input because the user is unlikely to want any of N identical bytes.
    // Side-effect: populates exactPairs so the final pass can classify groups correctly
    // when a bridging visual relationship is later discovered.
    private static IReadOnlyList<DuplicateGroup> BuildProvisionalExactGroups(
        IReadOnlyList<Photo> hashed,
        HashSet<(int, int)> exactPairs)
    {
        PopulateExactPairs(hashed, exactPairs);

        if (exactPairs.Count == 0) return Array.Empty<DuplicateGroup>();

        var uf = new UnionFind(hashed.Count);
        foreach (var (a, b) in exactPairs)
            uf.Union(a, b);

        var rootGroups = uf.ToGroups();
        return rootGroups.Values
            .Where(indices => indices.Count >= 2)
            .Select(indices =>
            {
                var photos = indices.Select(i => hashed[i]).ToList();
                return new DuplicateGroup
                {
                    Photos = photos,
                    Recommended = RecommendedPhotoSelector.Pick(photos),
                    Classification = GroupClassification.ExactOnly
                };
            })
            .OrderByDescending(g => g.Photos.Count)
            .ThenBy(g => g.Recommended.CaptureTime ?? DateTime.MaxValue)
            .ToList();
    }

    private static void PopulateExactPairs(IReadOnlyList<Photo> hashed, HashSet<(int, int)> exactPairs)
    {
        var byHash = new Dictionary<string, List<int>>();
        for (int i = 0; i < hashed.Count; i++)
        {
            var sha = hashed[i].Sha256;
            if (sha is null) continue;
            if (!byHash.TryGetValue(sha, out var list))
            {
                list = new List<int>();
                byHash[sha] = list;
            }
            list.Add(i);
        }
        // Emit the full clique of pairs within each SHA bucket (not just star-from-anchor).
        // UnionFind only needs the star form, but ClassifyGroup uses pair-membership lookup
        // to decide ExactOnly vs HasVisualComponent — if (B,C) is missing from exactPairs,
        // BuildGroups will add it to visualPairs (since byte-identical files trivially pass
        // the dHash threshold) and the whole group misclassifies as HasVisualComponent.
        foreach (var bucket in byHash.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
                for (int j = i + 1; j < bucket.Count; j++)
                    exactPairs.Add(NormalizePair(bucket[i], bucket[j]));
        }
    }

    private static (int, int) NormalizePair(int a, int b) => a < b ? (a, b) : (b, a);

    // For each HasVisualComponent group, find byte-identical sub-clusters (same SHA-256),
    // emit each sub-cluster of size > 1 as its own ExactOnly group (so the auto-resolver
    // picks it up), and rewrite the visual group's photo list to keep only the winner of
    // each sub-cluster plus the singletons. Visual groups that collapse to fewer than 2
    // surviving photos are dropped — they are no longer interesting for review.
    //
    // ExactOnly groups (already pure byte-identical) pass through unchanged.
    //
    // Uses SHA-256 rather than exactPairs/indices because at this point we have Photo
    // records, not the original integer indices used during pair construction. SHA-256
    // is the ground truth for byte-identity anyway, so this is the most direct check.
    // Photos with null Sha256 (unique file size, never hashed) cannot be exact duplicates
    // of anything and pass through with no extraction.
    private static List<DuplicateGroup> ExtractExactSubclustersFromVisualGroups(
        IReadOnlyList<DuplicateGroup> groups)
    {
        var output = new List<DuplicateGroup>();
        foreach (var group in groups)
        {
            if (group.Classification == GroupClassification.ExactOnly)
            {
                output.Add(group);
                continue;
            }

            var subclusters = group.Photos
                .Where(p => p.Sha256 is not null)
                .GroupBy(p => p.Sha256!)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            if (subclusters.Count == 0)
            {
                output.Add(group);
                continue;
            }

            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cluster in subclusters)
            {
                var winner = RecommendedPhotoSelector.Pick(cluster);
                output.Add(new DuplicateGroup
                {
                    Photos = cluster,
                    Recommended = winner,
                    Classification = GroupClassification.ExactOnly
                });
                foreach (var p in cluster)
                {
                    if (!string.Equals(p.Path, winner.Path, StringComparison.OrdinalIgnoreCase))
                        removed.Add(p.Path);
                }
            }

            var survivors = group.Photos
                .Where(p => !removed.Contains(p.Path))
                .ToList();
            if (survivors.Count >= 2)
            {
                output.Add(new DuplicateGroup
                {
                    Photos = survivors,
                    Recommended = RecommendedPhotoSelector.Pick(survivors),
                    Classification = GroupClassification.HasVisualComponent
                });
            }
        }
        return output;
    }

    // A group is ExactOnly iff every adjacency within it comes from exactPairs. Even a
    // single visualPair between any two members of the group escalates it to
    // HasVisualComponent — bridging makes human judgment required.
    private static GroupClassification ClassifyGroup(
        IReadOnlyList<int> indices,
        HashSet<(int, int)> exactPairs,
        HashSet<(int, int)> visualPairs)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            for (int j = i + 1; j < indices.Count; j++)
            {
                var pair = NormalizePair(indices[i], indices[j]);
                if (visualPairs.Contains(pair))
                    return GroupClassification.HasVisualComponent;
            }
        }
        // No visual pair found inside this group → all relationships are exact.
        return GroupClassification.ExactOnly;
    }

    private static IReadOnlyList<Photo> ComputeShaHashesForSizeMatches(
        IReadOnlyList<Photo> photos,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        var bySize = photos.GroupBy(p => p.FileSize).ToList();
        var needsHash = bySize.Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        var keepAsIs = bySize.Where(g => g.Count() == 1).SelectMany(g => g).ToList();
        var total = needsHash.Count;
        // Stage strings here are user-facing — they flow straight to the scan screen
        // via ScanningViewModel. Keep them plain-English (no "hash"/"SHA"/"perceptual" etc.).
        // Phase 2 splits into a setup label and a running label so the user sees movement
        // even when the actual SHA work is fast.
        var report = new Progress { Stage = "Checking file names and sizes", PhotosTotal = total };
        progress?.Report(report);

        var withSha = new List<Photo>(photos.Count);
        withSha.AddRange(keepAsIs);

        // Once we start actually hashing, switch the label so the user knows we're
        // doing real work, not still preparing.
        if (total > 0)
        {
            report.Stage = "Checking image resolution";
            progress?.Report(report);
        }

        int done = 0;
        foreach (var p in needsHash)
        {
            ct.ThrowIfCancellationRequested();
            withSha.Add(p with { Sha256 = ComputeSha256(p.Path) });
            done++;
            if (done % 10 == 0 || done == total)
            {
                report.PhotosProcessed = done;
                progress?.Report(report);
            }
        }

        return withSha;
    }

    private static IReadOnlyList<Photo> ComputeDHashes(
        IReadOnlyList<Photo> photos,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        // User-facing stage string — see notes above on plain-English requirement.
        var report = new Progress { Stage = "Comparing similar-looking photos", PhotosTotal = photos.Count };
        progress?.Report(report);

        var withHash = new Photo[photos.Count];
        int done = 0;
        Parallel.For(0, photos.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            try
            {
                var p = photos[i];
                var hash = DHash.Compute(p.Path);
                withHash[i] = p with { DHash = hash };
            }
            catch
            {
                withHash[i] = photos[i];
            }
            var d = Interlocked.Increment(ref done);
            // Throttle to every 5 photos so the determinate scan bar moves smoothly on
            // small-to-medium collections (10 visible updates on a 50-photo scan instead of 2).
            if (d % 5 == 0 || d == photos.Count)
            {
                lock (report)
                {
                    report.PhotosProcessed = d;
                    progress?.Report(report);
                }
            }
        });
        return withHash;
    }

    // Returns groups along with the indices that compose each one. Indices are needed
    // separately from Photo records because classification needs to look up pair-membership
    // in the HashSets, which are keyed by integer indices.
    private readonly record struct IndexedGroup(IReadOnlyList<int> Indices);

    private static List<IndexedGroup> BuildGroups(
        IReadOnlyList<Photo> photos,
        HashSet<(int, int)> exactPairs,
        HashSet<(int, int)> visualPairs,
        ScanTimings timings)
    {
        var uf = new UnionFind(photos.Count);

        // Phase 5 (union-find) timing accrues across the exact-pair replay below and the
        // ToGroups() materialization at the end; Phase 4 (the O(n²) pairwise loop) is timed
        // separately in between.
        var groupSw = Stopwatch.StartNew();

        // Exact-hash links — exactPairs already populated by BuildProvisionalExactGroups
        // or PopulateExactPairs. Replay them through UnionFind.
        foreach (var (a, b) in exactPairs)
            uf.Union(a, b);

        groupSw.Stop();

        // Perceptual links — naive O(n^2). Fine up to ~5K photos.
        // An exact pair always implies dHash similarity, but the exact relationship
        // is the stronger claim. Skip already-exact pairs so visualPairs stays clean
        // and ClassifyGroup can use it as ground truth: "any visualPair in this group
        // ⇒ HasVisualComponent". Without this skip, every byte-identical group would
        // misclassify as HasVisualComponent (would be a regression of Decision 3).
        var pairwiseSw = Stopwatch.StartNew();
        for (int i = 0; i < photos.Count; i++)
        {
            var hi = photos[i].DHash;
            if (hi is null) continue;
            for (int j = i + 1; j < photos.Count; j++)
            {
                var hj = photos[j].DHash;
                if (hj is null) continue;
                var pair = NormalizePair(i, j);
                if (exactPairs.Contains(pair)) continue;
                if (DHash.HammingDistance(hi.Value, hj.Value) <= DHash.SimilarityThresholdBits)
                {
                    visualPairs.Add(pair);
                    uf.Union(i, j);
                }
            }
        }
        pairwiseSw.Stop();

        groupSw.Start();
        var rootGroups = uf.ToGroups();
        var result = rootGroups.Values
            .Select(indices => new IndexedGroup(indices))
            .ToList();
        groupSw.Stop();

        timings.Pairwise = pairwiseSw.Elapsed;
        timings.GroupBuild = groupSw.Elapsed;
        return result;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }
}
