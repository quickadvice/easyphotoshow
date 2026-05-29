using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Models;
using ImageMagick;

namespace EasyPhotoShow.Core.Tests;

// Covers Tests 1 and 2 from the two-track-scan spec:
//   1. ExactOnly classification: two byte-identical files, no visual bridge → ExactOnly
//   2. HasVisualComponent classification: A=B exact, B≈C visual → merged group is HasVisualComponent
//
// Uses real on-disk image files so DuplicateDetector exercises its actual decode+hash paths
// instead of being mocked. Files are cleaned up in IDisposable Dispose.
public sealed class GroupClassificationTests : IDisposable
{
    private readonly string _workDir;
    private readonly List<string> _toCleanup = new();

    public GroupClassificationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"epstest_class_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Detect_TwoByteIdenticalFiles_ProducesExactOnlyGroup()
    {
        // Same bytes written twice → SHA-256 collision → exact group → no visual relationship
        // exists because we have only 2 photos and they're identical at the dHash level too,
        // but they're in the exactPairs set and that's how classification works.
        var pathA = WriteSolidImage("a.jpg", 200, 200, MagickColors.SteelBlue);
        var pathB = CopyFile(pathA, "b.jpg");

        var photos = new[] { MakePhoto(pathA), MakePhoto(pathB) };
        var detector = new DuplicateDetector();

        var result = detector.Detect(photos);

        Assert.Single(result.Groups);
        Assert.Equal(GroupClassification.ExactOnly, result.Groups[0].Classification);
        Assert.Single(result.ExactOnlyGroups);
        Assert.Empty(result.VisualGroups);
    }

    [Fact]
    public void Detect_ThreeByteIdenticalFiles_ProducesExactOnlyGroup()
    {
        // Regression: PopulateExactPairs originally emitted only star pairs from bucket[0],
        // so for 3 byte-identical files A,B,C it produced {(A,B),(A,C)} but not (B,C).
        // BuildGroups would then add (B,C) to visualPairs (byte-identical files trivially
        // pass the dHash threshold), and ClassifyGroup misclassified the whole group as
        // HasVisualComponent. The fix emits the full clique. This test would have caught
        // the original bug; the 2-photo case above does not exercise it.
        var pathA = WriteSolidImage("a.jpg", 200, 200, MagickColors.OliveDrab);
        var pathB = CopyFile(pathA, "b.jpg");
        var pathC = CopyFile(pathA, "c.jpg");

        var photos = new[] { MakePhoto(pathA), MakePhoto(pathB), MakePhoto(pathC) };
        var detector = new DuplicateDetector();

        var result = detector.Detect(photos);

        Assert.Single(result.Groups);
        Assert.Equal(3, result.Groups[0].Photos.Count);
        Assert.Equal(GroupClassification.ExactOnly, result.Groups[0].Classification);
        Assert.Single(result.ExactOnlyGroups);
        Assert.Empty(result.VisualGroups);
    }

    [Fact]
    public void Detect_ExactPlusVisualBridge_ExtractsExactAndKeepsVisualWithWinner()
    {
        // A == B (byte identical), B ≈ C (visually similar, not byte identical).
        // Union-find merges {A,B,C} and ClassifyGroup returns HasVisualComponent because
        // of the (A,C)/(B,C) visual pairs. Post-fix expectation:
        // ExtractExactSubclustersFromVisualGroups splits {A,B,C} into:
        //   - ExactOnly group {A, B} (winner=A) → auto-resolver moves B silently
        //   - HasVisualComponent group {A, C} → goes to review screen
        // The byte-identical pair never reaches the review UI. Regression for Bug 1B.
        var pathA = WriteSolidImage("a.jpg", 200, 200, MagickColors.SteelBlue);
        var pathB = CopyFile(pathA, "b.jpg");
        var pathC = WriteSolidImageWithSinglePixel("c.jpg", 200, 200,
            MagickColors.SteelBlue, MagickColors.OrangeRed);

        var photos = new[] { MakePhoto(pathA), MakePhoto(pathB), MakePhoto(pathC) };
        var detector = new DuplicateDetector();

        var result = detector.Detect(photos);

        Assert.Equal(2, result.Groups.Count);
        Assert.Single(result.ExactOnlyGroups);
        Assert.Single(result.VisualGroups);

        var exact = result.ExactOnlyGroups[0];
        Assert.Equal(2, exact.Photos.Count);
        Assert.Contains(exact.Photos, p => p.Path == pathA);
        Assert.Contains(exact.Photos, p => p.Path == pathB);

        var visual = result.VisualGroups[0];
        Assert.Equal(2, visual.Photos.Count);
        Assert.Contains(visual.Photos, p => p.Path == pathA);
        Assert.Contains(visual.Photos, p => p.Path == pathC);
        Assert.DoesNotContain(visual.Photos, p => p.Path == pathB);
    }

    [Fact]
    public void Detect_TwoExactSubclustersInOneVisualGroup_ExtractsBoth()
    {
        // A=B exact, C=D exact, A≈C visual. Union-find merges {A,B,C,D}. After extraction,
        // both exact sub-clusters become standalone ExactOnly groups and the visual group
        // reduces to one representative from each sub-cluster.
        var pathA = WriteSolidImage("a.jpg", 200, 200, MagickColors.SteelBlue);
        var pathB = CopyFile(pathA, "b.jpg");
        var pathC = WriteSolidImageWithSinglePixel("c.jpg", 200, 200,
            MagickColors.SteelBlue, MagickColors.OrangeRed);
        var pathD = CopyFile(pathC, "d.jpg");

        var photos = new[]
        {
            MakePhoto(pathA), MakePhoto(pathB),
            MakePhoto(pathC), MakePhoto(pathD)
        };
        var detector = new DuplicateDetector();

        var result = detector.Detect(photos);

        Assert.Equal(3, result.Groups.Count); // 2 exact + 1 visual
        Assert.Equal(2, result.ExactOnlyGroups.Count);
        Assert.Single(result.VisualGroups);

        var visual = result.VisualGroups[0];
        Assert.Equal(2, visual.Photos.Count);
        Assert.Contains(visual.Photos, p => p.Path == pathA || p.Path == pathB);
        Assert.Contains(visual.Photos, p => p.Path == pathC || p.Path == pathD);
    }

    private string WriteSolidImage(string name, int w, int h, IMagickColor<byte> color)
    {
        var path = Path.Combine(_workDir, name);
        using var img = new MagickImage(color, (uint)w, (uint)h);
        img.Format = MagickFormat.Jpeg;
        img.Quality = 90;
        img.Write(path);
        _toCleanup.Add(path);
        return path;
    }

    private string WriteSolidImageWithSinglePixel(string name, int w, int h,
        IMagickColor<byte> bg, IMagickColor<byte> pixel)
    {
        var path = Path.Combine(_workDir, name);
        using var img = new MagickImage(bg, (uint)w, (uint)h);
        using var pixels = img.GetPixelsUnsafe();
        pixels.SetPixel(0, 0, new[] { pixel.R, pixel.G, pixel.B });
        img.Format = MagickFormat.Jpeg;
        img.Quality = 90;
        img.Write(path);
        _toCleanup.Add(path);
        return path;
    }

    private string CopyFile(string source, string newName)
    {
        var dest = Path.Combine(_workDir, newName);
        File.Copy(source, dest, overwrite: true);
        _toCleanup.Add(dest);
        return dest;
    }

    private static Photo MakePhoto(string path)
    {
        var info = new FileInfo(path);
        return new Photo
        {
            Path = path,
            FileSize = info.Length,
            Format = PhotoFormat.Jpeg,
            VisualWidth = 200,
            VisualHeight = 200,
            CaptureTime = info.LastWriteTime
        };
    }
}
