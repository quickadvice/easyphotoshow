using System.Diagnostics;

namespace EasyPhotoShow.Core.Rendering;

public enum VideoEncoder
{
    H264Nvenc,
    H264Qsv,
    H264Amf,
    H264MediaFoundation,
    LibOpenH264
}

public static class EncoderProbe
{
    private static VideoEncoder? _cached;

    public static VideoEncoder Pick(CancellationToken ct = default)
    {
        if (_cached.HasValue) return _cached.Value;

        var listing = FFmpegEnvironment.RunCaptureAsync(FFmpegEnvironment.FFmpegPath, "-hide_banner -encoders", ct).GetAwaiter().GetResult();

        if (listing.Contains("h264_nvenc") && TestEncode("h264_nvenc"))
            return Cache(VideoEncoder.H264Nvenc);
        if (listing.Contains("h264_qsv") && TestEncode("h264_qsv"))
            return Cache(VideoEncoder.H264Qsv);
        if (listing.Contains("h264_amf") && TestEncode("h264_amf"))
            return Cache(VideoEncoder.H264Amf);
        if (listing.Contains("h264_mf") && TestEncode("h264_mf"))
            return Cache(VideoEncoder.H264MediaFoundation);
        return Cache(VideoEncoder.LibOpenH264);
    }

    private static VideoEncoder Cache(VideoEncoder e)
    {
        _cached = e;
        return e;
    }

    private static bool TestEncode(string encoder)
    {
        try
        {
            var args = $"-hide_banner -f lavfi -i color=black:s=320x240:d=0.1 -frames:v 1 -c:v {encoder} -f null -";
            var psi = new ProcessStartInfo(FFmpegEnvironment.FFmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string ToFFmpegArgs(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.H264Nvenc => "-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 23 -pix_fmt yuv420p",
        VideoEncoder.H264Qsv => "-c:v h264_qsv -preset medium -global_quality 23 -pix_fmt yuv420p",
        VideoEncoder.H264Amf => "-c:v h264_amf -quality balanced -rc cqp -qp_i 22 -qp_p 22 -pix_fmt yuv420p",
        VideoEncoder.H264MediaFoundation => "-c:v h264_mf -rate_control quality -quality 65 -pix_fmt yuv420p",
        VideoEncoder.LibOpenH264 => "-c:v libopenh264 -b:v 6M -pix_fmt yuv420p",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder))
    };
}
