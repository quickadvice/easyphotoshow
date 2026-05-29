using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Ordering;

namespace EasyPhotoShow.Core.Tests;

public class EventClustererTests
{
    [Fact]
    public void Cluster_TightTimes_SingleCluster()
    {
        var t0 = new DateTime(2026, 5, 25, 10, 0, 0);
        var photos = Enumerable.Range(0, 6).Select(i => MakePhoto($"p{i}.jpg", t0.AddSeconds(i * 30))).ToList();
        var clusters = EventClusterer.Cluster(photos);
        Assert.Single(clusters);
        Assert.Equal(6, clusters[0].Count);
    }

    [Fact]
    public void Cluster_GapsLargerThan30Min_StartsNewCluster()
    {
        var t0 = new DateTime(2026, 5, 25, 10, 0, 0);
        var photos = new List<Photo>
        {
            MakePhoto("a1.jpg", t0),
            MakePhoto("a2.jpg", t0.AddMinutes(5)),
            MakePhoto("b1.jpg", t0.AddHours(2)),
            MakePhoto("b2.jpg", t0.AddHours(2).AddMinutes(10)),
            MakePhoto("c1.jpg", t0.AddHours(8)),
        };
        var clusters = EventClusterer.Cluster(photos);
        Assert.Equal(3, clusters.Count);
        Assert.Equal(2, clusters[0].Count);
        Assert.Equal(2, clusters[1].Count);
        Assert.Single(clusters[2]);
    }

    [Fact]
    public void Cluster_NoCaptureTime_SingleClusterSortedByPath()
    {
        var photos = new List<Photo>
        {
            MakePhoto("z.jpg", captureTime: null),
            MakePhoto("a.jpg", captureTime: null),
            MakePhoto("m.jpg", captureTime: null),
        };
        var clusters = EventClusterer.Cluster(photos);
        Assert.Single(clusters);
        Assert.Equal(new[] { "a.jpg", "m.jpg", "z.jpg" }, clusters[0].Select(p => p.Path));
    }

    private static Photo MakePhoto(string path, DateTime? captureTime) => new()
    {
        Path = path,
        FileSize = 1000,
        Format = PhotoFormat.Jpeg,
        VisualWidth = 100,
        VisualHeight = 100,
        CaptureTime = captureTime
    };
}
