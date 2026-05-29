using EasyPhotoShow.Core.Imaging;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Rendering;

public sealed class StagingNormalizer
{
    // Output is 1080p (1920×1080). The cheap-blur fix renders the blur source at 480×270
    // (no detail preserved at any source resolution), and FFmpeg scales the foreground
    // to fit 1920×1080 regardless of the staged input size. Staging at 960px produces
    // visually identical 1080p output to staging at 1920px because:
    //   - The blurred background destroys all detail at any input resolution.
    //   - The foreground is bilinear-upscaled 960→1920 by FFmpeg at encode time, which
    //     is visually transparent at H.264 QSV's CQ=23 quality target for typical photos.
    // Reduction from 1920→960 (2026-05-26) cuts Magick.NET Resize CPU time by ~50% (4× fewer
    // pixels). Previous values: 2160 (initial), 1920 (post-cheap-blur), 960 (current).
    public const int MaxLongerSide = 960;
    public const int JpegQuality = 92;

    public sealed class Progress
    {
        public int Processed { get; set; }
        public int Total { get; set; }
    }

    public List<string> Normalize(
        IReadOnlyList<Photo> orderedPhotos,
        string stagingDir,
        IProgress<Progress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDir);
        var outputs = new string[orderedPhotos.Count];
        var report = new Progress { Total = orderedPhotos.Count };
        int done = 0;
        // Stage 1 runs during rendering (not scanning), so it has the CPU to itself —
        // raise parallelism from the conservative 2 that was set originally. Cap at 4 to
        // bound libheif memory pressure for HEIC-heavy collections (libheif is single-
        // threaded internally; too many concurrent HEIC decodes can spike memory).
        int workers = Math.Min(4, Math.Max(2, Environment.ProcessorCount - 1));

        Parallel.For(0, orderedPhotos.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers },
            i =>
            {
                var source = orderedPhotos[i].Path;
                var dest = Path.Combine(stagingDir, $"{i:D5}.jpg");
                NormalizedBitmapLoader.WriteJpeg(source, dest, MaxLongerSide, JpegQuality);
                outputs[i] = dest;

                int d = Interlocked.Increment(ref done);
                if (d % 5 == 0 || d == orderedPhotos.Count)
                {
                    lock (report)
                    {
                        report.Processed = d;
                        progress?.Report(report);
                    }
                }
            });

        return outputs.ToList();
    }
}
