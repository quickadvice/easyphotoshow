using EasyPhotoShow.Core.Imaging;
using ImageMagick;

namespace EasyPhotoShow.Core.Tests;

// Verifies the cross-cutting orientation guarantee: every photo coming out of
// LoadNormalizedBitmap (and WriteJpeg) presents its visually-correct dimensions
// regardless of the EXIF orientation tag on the source. Skipping or weakening
// this test risks shipping a "why is my video sideways" bug.
public class OrientationTests
{
    private static readonly (int? orientation, int srcW, int srcH, int expectedW, int expectedH)[] Cases =
    {
        (1, 200, 100, 200, 100),
        (3, 200, 100, 200, 100),
        (6, 200, 100, 100, 200), // rotated 90° CW: sensor 200x100 becomes visual 100x200
        (8, 200, 100, 100, 200), // rotated 90° CCW: same
    };

    [Fact]
    public void Load_AppliesOrientation_ToVisualDimensions()
    {
        foreach (var (orientation, srcW, srcH, expectedW, expectedH) in Cases)
        {
            var path = Path.Combine(Path.GetTempPath(), $"orient_{orientation}_{Guid.NewGuid():N}.jpg");
            try
            {
                CreateImageWithOrientation(path, srcW, srcH, orientation!.Value);
                var loaded = NormalizedBitmapLoader.Load(path);
                Assert.True(
                    loaded.Width == expectedW && loaded.Height == expectedH,
                    $"orientation={orientation}: expected {expectedW}x{expectedH}, got {loaded.Width}x{loaded.Height}");
            }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void WriteJpeg_StripsOrientationTag()
    {
        var src = Path.Combine(Path.GetTempPath(), $"orient_src_{Guid.NewGuid():N}.jpg");
        var dst = Path.Combine(Path.GetTempPath(), $"orient_dst_{Guid.NewGuid():N}.jpg");
        try
        {
            CreateImageWithOrientation(src, 400, 200, orientation: 6);
            NormalizedBitmapLoader.WriteJpeg(src, dst, maxLongerSidePx: 1000, quality: 92);

            using var image = new MagickImage(dst);
            var profile = image.GetExifProfile();
            int orientation = profile is null ? 1 : (int)(profile.GetValue(ExifTag.Orientation)?.Value ?? (ushort)1);
            Assert.Equal(1, orientation);
            Assert.Equal(200u, image.Width);
            Assert.Equal(400u, image.Height);
        }
        finally
        {
            File.Delete(src); File.Delete(dst);
        }
    }

    private static void CreateImageWithOrientation(string path, int width, int height, int orientation)
    {
        using var img = new MagickImage(MagickColors.SteelBlue, (uint)width, (uint)height);
        img.Format = MagickFormat.Jpeg;
        img.Orientation = (OrientationType)orientation;

        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)orientation);
        img.SetProfile(profile);
        img.Write(path);

        // Sanity check the file we just wrote actually has the orientation tag.
        using var verify = new MagickImage(path);
        if ((int)verify.Orientation != orientation)
            throw new InvalidOperationException(
                $"Test fixture is broken: wrote orientation={orientation} but file reports {(int)verify.Orientation}.");
    }
}
