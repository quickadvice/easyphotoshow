using ImageMagick;

namespace EasyPhotoShow.Core.Imaging;

// Difference hash (dHash) — 64-bit perceptual fingerprint.
// Resize to 9x8 grayscale, compare each pixel to its right neighbor.
// Two images with Hamming distance <= 8 are treated as visually similar.
public static class DHash
{
    public const int SimilarityThresholdBits = 8;

    public static ulong Compute(string path)
    {
        using var image = new MagickImage(path);
        return Compute(image);
    }

    public static async Task<ulong> ComputeAsync(string path, CancellationToken ct = default)
    {
        var image = new MagickImage();
        await image.ReadAsync(path, ct).ConfigureAwait(false);
        try
        {
            return Compute(image);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static ulong Compute(MagickImage image)
    {
        image.AutoOrient();
        image.ColorSpace = ColorSpace.Gray;
        image.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });

        var pixels = image.GetPixelsUnsafe().ToByteArray(PixelMapping.RGB)
            ?? throw new InvalidOperationException("Failed to read pixel data for dHash.");

        ulong hash = 0;
        int bit = 63;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int leftIdx = (y * 9 + x) * 3;
                int rightIdx = (y * 9 + x + 1) * 3;
                byte left = pixels[leftIdx];
                byte right = pixels[rightIdx];
                if (left > right)
                    hash |= 1UL << bit;
                bit--;
            }
        }
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b)
    {
        return System.Numerics.BitOperations.PopCount(a ^ b);
    }
}
