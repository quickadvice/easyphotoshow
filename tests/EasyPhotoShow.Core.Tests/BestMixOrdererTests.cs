using System.Diagnostics;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Ordering;

namespace EasyPhotoShow.Core.Tests;

// Asserts the spacing-based BestMixOrderer contract introduced 2026-05-26 —
// replacing the prior event-clustering tests, which validated the now-removed
// "single-event chronological fallback" and "max run of N from same event"
// behaviors. Those tests asserted the wrong product behavior (sections).
public class BestMixOrdererTests
{
    // Test 1: 20 photos in a single similarity cluster within a 100-photo set
    // should land at least 3 slots apart from each other.
    [Fact]
    public void SimilarPhotos_AreSpacedApart()
    {
        const int similarCount = 20;
        const int totalCount = 100;
        const ulong sharedHash = 0xAAAA_AAAA_AAAA_AAAAUL;

        var sharedSession = new DateTime(2026, 1, 1, 12, 0, 0);
        var similar = Enumerable.Range(0, similarCount).Select(i => new Photo
        {
            Path = $"osu_{i:00}.jpg",
            FileSize = 1000,
            Format = PhotoFormat.Jpeg,
            VisualWidth = 100,
            VisualHeight = 100,
            CaptureTime = sharedSession.AddSeconds(i),
            DHash = sharedHash
        }).ToList();

        // 80 visually-distinct photos spread across different times so they don't
        // co-cluster with each other or with the OSU cluster.
        var others = Enumerable.Range(0, totalCount - similarCount).Select(i => new Photo
        {
            Path = $"other_{i:000}.jpg",
            FileSize = 1000,
            Format = PhotoFormat.Jpeg,
            VisualWidth = 100,
            VisualHeight = 100,
            CaptureTime = new DateTime(2026, 2, 1).AddHours(i),  // 1h apart → no time cluster
            DHash = (ulong)i << 32 | 0x5555UL                     // distinct dHashes far from sharedHash
        }).ToList();

        var input = similar.Concat(others).ToList();
        var ordered = BestMixOrderer.Order(input);

        // Find the slot index of each OSU photo.
        var similarPaths = new HashSet<string>(similar.Select(p => p.Path));
        var positions = new List<int>();
        for (int i = 0; i < ordered.Count; i++)
            if (similarPaths.Contains(ordered[i].Path)) positions.Add(i);
        positions.Sort();

        Assert.Equal(similarCount, positions.Count);

        for (int k = 1; k < positions.Count; k++)
        {
            int gap = positions[k] - positions[k - 1];
            Assert.True(gap > 3,
                $"Similar photos at slots {positions[k - 1]} and {positions[k]} are only {gap} apart " +
                $"(expected > 3). Full positions: [{string.Join(", ", positions)}]");
        }
    }

    // Test 2: every input photo appears exactly once in the output, no extras.
    [Fact]
    public void AllPhotosPresent()
    {
        var photos = MakeMixedPhotos(50);
        var ordered = BestMixOrderer.Order(photos);

        Assert.Equal(photos.Count, ordered.Count);
        Assert.Equal(
            photos.Select(p => p.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            ordered.Select(p => p.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    // Test 3: same input → same output, byte-identical, two consecutive runs.
    [Fact]
    public void IsDeterministic()
    {
        var photos = MakeMixedPhotos(50);
        var a = BestMixOrderer.Order(photos);
        var b = BestMixOrderer.Order(photos);
        Assert.Equal(a.Select(p => p.Path), b.Select(p => p.Path));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(BestMixOrderer.Order(Array.Empty<Photo>()));
    }

    [Fact]
    public void SinglePhoto_ReturnsSinglePhoto()
    {
        var p = new Photo
        {
            Path = "solo.jpg",
            FileSize = 1000,
            Format = PhotoFormat.Jpeg,
            VisualWidth = 100,
            VisualHeight = 100
        };
        var ordered = BestMixOrderer.Order(new[] { p });
        Assert.Single(ordered);
        Assert.Equal(p.Path, ordered[0].Path);
    }

    // Test 6: when there's no dHash and no capture time, the only deterministic
    // answer is alphabetical path order.
    [Fact]
    public void NoDHash_NoCaptureTime_ReturnsAlphabetical()
    {
        var photos = new[] { "c.jpg", "a.jpg", "b.jpg" }.Select(path => new Photo
        {
            Path = path,
            FileSize = 1000,
            Format = PhotoFormat.Jpeg,
            VisualWidth = 100,
            VisualHeight = 100
            // No DHash, no CaptureTime
        }).ToList();

        var ordered = BestMixOrderer.Order(photos);

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, ordered.Select(p => p.Path));
    }

    // Test 7: 1,000 photos with deterministic-pseudo-random dHashes must order
    // in under 200 ms. Includes a warm-up run because first-call cost in .NET
    // pays JIT + collection allocation overhead that's not representative of
    // steady-state performance.
    [Fact]
    public void LargeCollection_CompletesUnder200ms()
    {
        var photos = MakeDeterministicLargeSet(1000);

        // Warm-up to JIT the algorithm path and avoid first-call noise.
        BestMixOrderer.Order(photos);

        var sw = Stopwatch.StartNew();
        var ordered = BestMixOrderer.Order(photos);
        sw.Stop();

        Assert.Equal(1000, ordered.Count);
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"BestMixOrderer.Order took {sw.ElapsedMilliseconds} ms for 1,000 photos (budget: 200 ms).");
    }

    // Builds a mix of dHash/time-clustered and singleton photos that exercises
    // the union-find + spacing logic without relying on any specific cluster shape.
    private static List<Photo> MakeMixedPhotos(int count)
    {
        var baseTime = new DateTime(2026, 1, 1);
        var photos = new List<Photo>(count);
        for (int i = 0; i < count; i++)
        {
            // Every 7th photo shares a dHash bucket with the prior 6 → forms small clusters.
            ulong dHash = (ulong)(i / 7) << 8;
            photos.Add(new Photo
            {
                Path = $"p_{i:000}.jpg",
                FileSize = 1000 + i,
                Format = PhotoFormat.Jpeg,
                VisualWidth = 100,
                VisualHeight = 100,
                CaptureTime = baseTime.AddHours(i),
                DHash = dHash
            });
        }
        return photos;
    }

    // Deterministic pseudo-random dHashes from a fixed seed so the test is
    // reproducible across machines without using System.Random's wall-clock seeding.
    private static List<Photo> MakeDeterministicLargeSet(int count)
    {
        // SplitMix64-style hash gives well-distributed 64-bit outputs.
        ulong state = 0xDEAD_BEEF_CAFE_F00DUL;
        ulong NextHash()
        {
            state += 0x9E37_79B9_7F4A_7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }

        var baseTime = new DateTime(2026, 1, 1);
        var photos = new List<Photo>(count);
        for (int i = 0; i < count; i++)
        {
            photos.Add(new Photo
            {
                Path = $"big_{i:0000}.jpg",
                FileSize = 1000 + i,
                Format = PhotoFormat.Jpeg,
                VisualWidth = 100,
                VisualHeight = 100,
                CaptureTime = baseTime.AddMinutes(i * 47),  // ~47 min apart → mostly non-co-session
                DHash = NextHash()
            });
        }
        return photos;
    }
}
