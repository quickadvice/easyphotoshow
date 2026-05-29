using EasyPhotoShow.Core.Imaging;
using ImageMagick;

namespace EasyPhotoShow.Core.Tests;

public class DHashTests
{
    [Fact]
    public void HammingDistance_Identical_IsZero()
    {
        Assert.Equal(0, DHash.HammingDistance(0xDEADBEEFCAFE0001UL, 0xDEADBEEFCAFE0001UL));
    }

    [Fact]
    public void HammingDistance_FullyOpposite_Is64()
    {
        Assert.Equal(64, DHash.HammingDistance(0x0UL, ulong.MaxValue));
    }

    [Fact]
    public void Compute_DeterministicForSameImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dhash_{Guid.NewGuid():N}.png");
        try
        {
            CreateGradient(path, 200, 150);
            var a = DHash.Compute(path);
            var b = DHash.Compute(path);
            Assert.Equal(a, b);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compute_DifferentImagesProduceDifferentHashes()
    {
        var p1 = Path.Combine(Path.GetTempPath(), $"dhash1_{Guid.NewGuid():N}.png");
        var p2 = Path.Combine(Path.GetTempPath(), $"dhash2_{Guid.NewGuid():N}.png");
        try
        {
            CreateGradient(p1, 200, 150);
            CreateChecker(p2, 200, 150);
            var a = DHash.Compute(p1);
            var b = DHash.Compute(p2);
            Assert.True(DHash.HammingDistance(a, b) > DHash.SimilarityThresholdBits,
                $"Expected gradient vs checker to be visually different, but distance was {DHash.HammingDistance(a, b)}");
        }
        finally
        {
            File.Delete(p1); File.Delete(p2);
        }
    }

    private static void CreateGradient(string path, int width, int height)
    {
        using var img = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                byte v = (byte)(255 * x / width);
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v;
            }
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB);
        using var img2 = new MagickImage(pixels, settings);
        img2.Format = MagickFormat.Png;
        img2.Write(path);
    }

    private static void CreateChecker(string path, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                byte v = ((x / 16) + (y / 16)) % 2 == 0 ? (byte)0 : (byte)255;
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v;
            }
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB);
        using var img = new MagickImage(pixels, settings);
        img.Format = MagickFormat.Png;
        img.Write(path);
    }
}
