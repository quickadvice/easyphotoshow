using EasyPhotoShow.Core.Utilities;

namespace EasyPhotoShow.Core.Tests;

// Covers Test 4 from the spec: rolling window caps at 5 items.
// RollingWindow is a general-purpose Core utility; verifying cap behavior here keeps
// the test in Core (no WPF dependency) while still covering the load-bearing invariant.
public class RollingWindowTests
{
    [Fact]
    public void Add_RespectsCapacity_DroppingOldestFirst()
    {
        var window = new RollingWindow<int>(capacity: 5);

        // Add 8 items. After the 6th, the window should evict in FIFO order.
        for (int i = 1; i <= 8; i++)
            window.Add(i);

        Assert.Equal(5, window.Count);
        // Last 5 items survive: 4, 5, 6, 7, 8 (in insertion order).
        Assert.Equal(new[] { 4, 5, 6, 7, 8 }, window.Snapshot());
    }

    [Fact]
    public void Add_ReturnsEvictedItem_WhenFull()
    {
        // Use a reference type so reference-type semantics (default == null) are
        // exercised, matching how the window is used with class-typed items.
        var window = new RollingWindow<string>(capacity: 3);
        Assert.Null(window.Add("a"));
        Assert.Null(window.Add("b"));
        Assert.Null(window.Add("c"));
        Assert.Equal("a", window.Add("d")); // "a" evicted
        Assert.Equal("b", window.Add("e")); // "b" evicted
    }

    [Fact]
    public void DefaultState_IsEmpty()
    {
        // Covers Test 5: when no exact groups arrive, the section is hidden because
        // the underlying queue is empty (and the VM's ShowExactCopySection flag is false).
        var window = new RollingWindow<int>(capacity: 5);
        Assert.Equal(0, window.Count);
        Assert.Empty(window.Snapshot());
    }

    [Fact]
    public void Clear_EmptiesTheWindow()
    {
        var window = new RollingWindow<int>(capacity: 3);
        window.Add(1);
        window.Add(2);
        window.Clear();
        Assert.Equal(0, window.Count);
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow<int>(-1));
    }
}
