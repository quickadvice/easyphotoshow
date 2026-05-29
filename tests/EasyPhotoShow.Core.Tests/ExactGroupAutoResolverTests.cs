using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Tests;

// Covers Test 3 from the spec: auto-resolution removes the non-recommended photo
// from the "PhotosForSlideshow" list returned by the helper. The Core helper is
// what ScanningViewModel calls before writing to App.Session.PhotosForSlideshow,
// so verifying the helper's output is equivalent to verifying the session update
// without dragging WPF / App.Session into the test.
public sealed class ExactGroupAutoResolverTests : IDisposable
{
    private readonly string _workDir;

    public ExactGroupAutoResolverTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"epstest_autoresolve_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_ExactGroupOf2_RemovesNonRecommendedFromList_AndMovesFile()
    {
        // Recommended: higher resolution wins (1000x1000 > 500x500 by area).
        // The non-recommended should be moved AND removed from the slideshow list.
        var kept = WriteFile("kept.jpg", "kept content");
        var dup = WriteFile("dup.jpg", "kept content"); // same bytes — same file actually

        var keptPhoto = new Photo
        {
            Path = kept, FileSize = new FileInfo(kept).Length,
            Format = PhotoFormat.Jpeg, VisualWidth = 1000, VisualHeight = 1000
        };
        var dupPhoto = new Photo
        {
            Path = dup, FileSize = new FileInfo(dup).Length,
            Format = PhotoFormat.Jpeg, VisualWidth = 500, VisualHeight = 500
        };

        var group = new DuplicateGroup
        {
            Photos = new[] { keptPhoto, dupPhoto },
            Recommended = keptPhoto,
            Classification = GroupClassification.ExactOnly
        };

        var allPhotos = new[] { keptPhoto, dupPhoto };

        var result = ExactGroupAutoResolver.Resolve(
            exactOnlyGroups: new[] { group },
            currentPhotosForSlideshow: allPhotos,
            sourceFolders: new[] { _workDir });

        // The non-recommended path must be absent from RemainingPhotos.
        Assert.DoesNotContain(result.RemainingPhotos, p => p.Path.Equals(dup, StringComparison.OrdinalIgnoreCase));
        // The recommended must still be there.
        Assert.Contains(result.RemainingPhotos, p => p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.GroupsResolved);
        Assert.Equal(1, result.PhotosMoved);
        // The duplicate file should now exist under PotentialDuplicates, not at its
        // original location.
        Assert.False(File.Exists(dup), "Duplicate file should have been moved out of its source location.");
        var movedPath = Path.Combine(_workDir, "PotentialDuplicates", "dup.jpg");
        Assert.True(File.Exists(movedPath), $"Duplicate file should be at {movedPath}");
    }

    [Fact]
    public void Resolve_EmptyGroups_ReturnsListUnchanged()
    {
        var photo = new Photo
        {
            Path = "any.jpg", FileSize = 1,
            Format = PhotoFormat.Jpeg, VisualWidth = 100, VisualHeight = 100
        };
        var list = new[] { photo };

        var result = ExactGroupAutoResolver.Resolve(
            exactOnlyGroups: Array.Empty<DuplicateGroup>(),
            currentPhotosForSlideshow: list,
            sourceFolders: new[] { _workDir });

        Assert.Same(list, result.RemainingPhotos); // unchanged reference is fine here
        Assert.Equal(0, result.GroupsResolved);
        Assert.Equal(0, result.PhotosMoved);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
