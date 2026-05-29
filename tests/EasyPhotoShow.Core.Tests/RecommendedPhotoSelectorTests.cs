using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Tests;

public class RecommendedPhotoSelectorTests
{
    [Fact]
    public void Pick_HigherResolution_Wins()
    {
        var small = MakePhoto("small.jpg", 1000, 1000, fileSize: 5000);
        var large = MakePhoto("large.jpg", 2000, 2000, fileSize: 1000);
        var winner = RecommendedPhotoSelector.Pick(new[] { small, large });
        Assert.Same(large, winner);
    }

    [Fact]
    public void Pick_SameResolution_LargerFileWins()
    {
        var lossy = MakePhoto("lossy.jpg", 1000, 1000, fileSize: 100_000);
        var pristine = MakePhoto("pristine.jpg", 1000, 1000, fileSize: 500_000);
        var winner = RecommendedPhotoSelector.Pick(new[] { lossy, pristine });
        Assert.Same(pristine, winner);
    }

    [Fact]
    public void Pick_TieBreakers_AreDeterministic()
    {
        var a = MakePhoto("a.jpg", 1000, 1000, fileSize: 100, captureTime: new DateTime(2026, 1, 1));
        var b = MakePhoto("b.jpg", 1000, 1000, fileSize: 100, captureTime: new DateTime(2026, 1, 1));
        var first = RecommendedPhotoSelector.Pick(new[] { a, b });
        var second = RecommendedPhotoSelector.Pick(new[] { b, a });
        Assert.Same(first, second.Path == first.Path ? second : first);
        Assert.Equal(first.Path, second.Path);
    }

    private static Photo MakePhoto(string path, int w, int h, long fileSize, DateTime? captureTime = null) => new()
    {
        Path = path,
        FileSize = fileSize,
        Format = PhotoFormat.Jpeg,
        VisualWidth = w,
        VisualHeight = h,
        CaptureTime = captureTime
    };
}
