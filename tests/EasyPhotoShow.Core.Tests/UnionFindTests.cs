using EasyPhotoShow.Core.DuplicateDetection;

namespace EasyPhotoShow.Core.Tests;

public class UnionFindTests
{
    [Fact]
    public void Union_ConnectedNodes_ShareSameGroup()
    {
        var uf = new UnionFind(6);
        uf.Union(0, 1);
        uf.Union(1, 2);
        uf.Union(3, 4);

        var groups = uf.ToGroups();
        Assert.Equal(3, groups.Count); // {0,1,2}, {3,4}, {5}
        Assert.Contains(groups.Values, g => g.OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 }));
        Assert.Contains(groups.Values, g => g.OrderBy(x => x).SequenceEqual(new[] { 3, 4 }));
        Assert.Contains(groups.Values, g => g.SequenceEqual(new[] { 5 }));
    }

    [Fact]
    public void Union_TransitiveLinks_BridgesGroups()
    {
        var uf = new UnionFind(5);
        uf.Union(0, 1);
        uf.Union(2, 3);
        uf.Union(1, 2);

        var groups = uf.ToGroups();
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups.Values, g => g.OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2, 3 }));
    }
}
