using System.Diagnostics;

namespace EasyPhotoShow.Core.Rendering;

public sealed class FFmpegMissingException : Exception
{
    public FFmpegMissingException(string message) : base(message) { }
}

public static class FFmpegEnvironment
{
    private static string? _cachedFFmpeg;
    private static string? _cachedFFprobe;

    public static string FFmpegPath => _cachedFFmpeg ??= Resolve("ffmpeg.exe");
    public static string FFprobePath => _cachedFFprobe ??= Resolve("ffprobe.exe");

    public static bool IsAvailable()
    {
        try
        {
            _ = FFmpegPath;
            _ = FFprobePath;
            return true;
        }
        catch (FFmpegMissingException)
        {
            return false;
        }
    }

    private static string Resolve(string exe)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", exe);
        if (File.Exists(bundled)) return bundled;

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        throw new FFmpegMissingException(
            $"EasyPhotoShow could not find {exe}. Place an LGPL build of FFmpeg into 'tools/ffmpeg/' beside the application, or add ffmpeg to the system PATH.");
    }

    internal static async Task<string> RunCaptureAsync(string exePath, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exePath}.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return stdout + stderr;
    }
}
