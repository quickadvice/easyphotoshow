using System.Diagnostics;
using EasyPhotoShow.Core.Models;
using ImageMagick;
using ImageMagick.Formats;

namespace EasyPhotoShow.Core.Imaging;

// Single source of truth for image decode + EXIF orientation in EasyPhotoShow.
// FFmpeg does not auto-rotate still-image inputs, so portrait iPhone shots
// (orientation tag = 6) appear sideways in the MP4 unless we resolve here.
// Every component that decodes a photo MUST go through this class.
public static class NormalizedBitmapLoader
{
    public static NormalizedBitmap Load(string path, int? maxLongerSidePx = null)
    {
        using var image = new MagickImage(path);
        return Normalize(image, maxLongerSidePx);
    }

    public static async Task<NormalizedBitmap> LoadAsync(string path, int? maxLongerSidePx = null, CancellationToken ct = default)
    {
        var image = new MagickImage();
        await image.ReadAsync(path, ct).ConfigureAwait(false);
        try
        {
            return Normalize(image, maxLongerSidePx);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static NormalizedBitmap Normalize(MagickImage image, int? maxLongerSidePx)
    {
        image.AutoOrient();
        image.ColorSpace = ColorSpace.sRGB;

        if (maxLongerSidePx is int max && (image.Width > max || image.Height > max))
        {
            var size = new MagickGeometry((uint)max, (uint)max) { Greater = true };
            image.Resize(size);
        }

        image.Format = MagickFormat.Rgba;
        var pixels = image.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("Failed to read pixel data.");

        return new NormalizedBitmap
        {
            Width = (int)image.Width,
            Height = (int)image.Height,
            PixelsRgba = pixels
        };
    }

    public static void WriteJpeg(string sourcePath, string destPath, int maxLongerSidePx, int quality)
    {
        // Per-phase timing: Stopwatch.GetTimestamp avoids the Stopwatch object allocation
        // we'd pay 6× per photo with Stopwatch.StartNew(). Interlocked.Add aggregates safely
        // across the parallel workers in StagingNormalizer. Phase costs are visible via the
        // [TIMING-S1] log block printed by RenderJob after Stage 1 completes.
        long t = Stopwatch.GetTimestamp();
        var image = new MagickImage(sourcePath);
        Interlocked.Add(ref StagingTimings.FileReadTicks, Stopwatch.GetTimestamp() - t);
        try
        {
            t = Stopwatch.GetTimestamp();
            image.AutoOrient();
            Interlocked.Add(ref StagingTimings.AutoOrientTicks, Stopwatch.GetTimestamp() - t);

            t = Stopwatch.GetTimestamp();
            image.ColorSpace = ColorSpace.sRGB;
            Interlocked.Add(ref StagingTimings.ColorSpaceTicks, Stopwatch.GetTimestamp() - t);

            // Force consistent TrueColor output so FFmpeg always sees the same pixel format.
            // Without this, grayscale source photos produce grayscale JPEGs and yuvj444p
            // mixed inputs cause swscaler warnings + worst-case filtergraph instability.
            t = Stopwatch.GetTimestamp();
            image.ColorType = ColorType.TrueColor;
            Interlocked.Add(ref StagingTimings.ColorTypeTicks, Stopwatch.GetTimestamp() - t);

            t = Stopwatch.GetTimestamp();
            if (image.Width > maxLongerSidePx || image.Height > maxLongerSidePx)
            {
                var size = new MagickGeometry((uint)maxLongerSidePx, (uint)maxLongerSidePx) { Greater = true };
                image.Resize(size);
            }
            Interlocked.Add(ref StagingTimings.ResizeTicks, Stopwatch.GetTimestamp() - t);

            t = Stopwatch.GetTimestamp();
            image.Quality = (uint)quality;
            // Force 4:2:0 chroma sub-sampling on the JPEG so FFmpeg always reads yuvj420p
            // and never needs swscaler to convert from yuvj422p / yuvj444p / yuvj440p. This
            // suppresses ~36 "deprecated pixel format used" warnings per chunk. Grayscale
            // source photos still produce a 'gray' colorspace JPEG that swscaler must
            // promote — that's one warning per grayscale photo, which is expected.
            image.Settings.SetDefines(new JpegWriteDefines { SamplingFactor = JpegSamplingFactor.Ratio420 });
            image.Format = MagickFormat.Jpeg;
            image.Strip();
            image.Write(destPath);
            Interlocked.Add(ref StagingTimings.StripWriteTicks, Stopwatch.GetTimestamp() - t);
        }
        finally
        {
            image.Dispose();
        }
    }
}

// Diagnostic accumulators for Stage 1 per-phase timing. Reset by RenderJob.RunAsync before
// Stage 1, snapshotted + logged after. Not user-facing.
public static class StagingTimings
{
    public static long FileReadTicks;
    public static long AutoOrientTicks;
    public static long ColorSpaceTicks;
    public static long ColorTypeTicks;
    public static long ResizeTicks;
    public static long StripWriteTicks;

    public static void Reset()
    {
        Interlocked.Exchange(ref FileReadTicks, 0);
        Interlocked.Exchange(ref AutoOrientTicks, 0);
        Interlocked.Exchange(ref ColorSpaceTicks, 0);
        Interlocked.Exchange(ref ColorTypeTicks, 0);
        Interlocked.Exchange(ref ResizeTicks, 0);
        Interlocked.Exchange(ref StripWriteTicks, 0);
    }

    public readonly record struct Snapshot(
        long FileReadTicks,
        long AutoOrientTicks,
        long ColorSpaceTicks,
        long ColorTypeTicks,
        long ResizeTicks,
        long StripWriteTicks);

    public static Snapshot Read() => new(
        Interlocked.Read(ref FileReadTicks),
        Interlocked.Read(ref AutoOrientTicks),
        Interlocked.Read(ref ColorSpaceTicks),
        Interlocked.Read(ref ColorTypeTicks),
        Interlocked.Read(ref ResizeTicks),
        Interlocked.Read(ref StripWriteTicks));
}
