using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Rendering;
using ImageMagick;

namespace EasyPhotoShow.Core.Tests;

// End-to-end smoke test for the full render pipeline (Normalize → Render → Finalize).
// Auto-skips if FFmpeg isn't on PATH or under tools/ffmpeg/ — local-only by design.
public class RenderJobIntegrationTests
{
    private static bool FFmpegBundled()
    {
        // Manually probe both PATH and the App's tools/ffmpeg/ location so the test
        // works regardless of which Directory.SetCurrentDirectory the test runner used.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "EasyPhotoShow.App", "tools", "ffmpeg", "ffmpeg.exe")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var dir = Path.GetDirectoryName(c)!;
                Environment.SetEnvironmentVariable("PATH",
                    dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
                return true;
            }
        }
        return FFmpegEnvironment.IsAvailable();
    }

    [Fact]
    public async Task RenderJob_ProducesValidMp4_FromSyntheticPhotos()
        => await RunRenderTestAsync(photoCount: 3, secondsPerPhoto: 1.5);

    [Fact]
    public async Task RenderJob_ProducesValidMp4_AcrossMultipleChunks()
        // Minimum multi-chunk coverage: photoCount must exceed PhotosPerChunk (currently
        // 12) to trigger the concat pass. 14 photos → 2 chunks (12 + 2), runs in 1
        // parallel round (both chunks together). Smaller chunks = lower per-chunk filter
        // graph memory, mitigating "Cannot allocate memory" errors observed on machines
        // with constrained RAM during sustained iterative test runs.
        //
        // If PhotosPerChunk grows in future, bump this count to (PhotosPerChunk + 2) to
        // continue exercising the multi-chunk + concat path.
        => await RunRenderTestAsync(photoCount: 14, secondsPerPhoto: 0.4);

    private static async Task RunRenderTestAsync(int photoCount, double secondsPerPhoto)
    {
        if (!FFmpegBundled()) return;

        var workDir = Path.Combine(Path.GetTempPath(), $"epstest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var palette = new IMagickColor<byte>[]
            {
                MagickColors.SteelBlue, MagickColors.OrangeRed, MagickColors.ForestGreen,
                MagickColors.Goldenrod, MagickColors.MediumPurple, MagickColors.Teal,
                MagickColors.Crimson, MagickColors.DarkSlateGray
            };

            var photos = new List<Photo>();
            for (int i = 0; i < photoCount; i++)
            {
                var path = Path.Combine(workDir, $"src_{i:D3}.jpg");
                // Mix aspect ratios to exercise the blurred-fill path on portrait + landscape inputs
                (int w, int h) = (i % 3) switch
                {
                    0 => (640, 480),
                    1 => (480, 640),
                    _ => (800, 200)
                };
                CreateColoredImage(path, w, h, palette[i % palette.Length]);
                photos.Add(new Photo
                {
                    Path = path,
                    FileSize = new FileInfo(path).Length,
                    Format = PhotoFormat.Jpeg,
                    VisualWidth = w,
                    VisualHeight = h,
                    CaptureTime = new DateTime(2026, 5, 25, 10, 0, i)
                });
            }

            var settings = new SlideshowSettings
            {
                Photos = photos,
                SecondsPerPhoto = secondsPerPhoto,
                Ordering = PhotoOrdering.KeepFolderOrder,
                Transition = TransitionStyle.Fade,
                Music = MusicChoice.None(),
                SlideshowName = $"Smoke_{photoCount}",
                SaveFolder = workDir
            };

            var progressReports = new List<RenderProgress>();
            var progress = new Progress<RenderProgress>(p => { lock (progressReports) progressReports.Add(p); });

            await new RenderJob().RunAsync(settings, photos, progress, CancellationToken.None);

            Assert.True(File.Exists(settings.OutputPath), $"Expected MP4 at {settings.OutputPath}");
            Assert.True(new FileInfo(settings.OutputPath).Length > 4096);
            Assert.Contains(progressReports, p => p.Stage == RenderStage.Complete);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static void CreateColoredImage(string path, int width, int height, IMagickColor<byte> color)
    {
        using var img = new MagickImage(color, (uint)width, (uint)height);
        img.Format = MagickFormat.Jpeg;
        img.Quality = 90;
        img.Write(path);
    }
}
