using System.Diagnostics;
using System.Globalization;

namespace EasyPhotoShow.Core.Rendering;

public static class MusicMetadataProbe
{
    // Returns the audio file's duration via ffprobe. Null if probe failed or FFmpeg is missing.
    public static TimeSpan? GetDuration(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ffprobe = FFmpegEnvironment.FFprobePath;
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0) return null;
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
            return null;
        }
        catch
        {
            return null;
        }
    }
}
