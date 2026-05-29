using System.Globalization;
using System.Text;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Rendering;

internal static class FilterGraphBuilder
{
    public const int OutputWidth = 1920;
    public const int OutputHeight = 1080;
    public const int FrameRate = 30;
    public const double TransitionSeconds = 1.0;
    // Blur is done at small resolution then scaled up — visually identical to blurring at full
    // size (blur destroys detail anyway) but ~16× cheaper in memory and CPU. Critical for fitting
    // larger chunks into FFmpeg's filtergraph budget without OOM.
    public const int BlurSourceWidth = 480;
    public const int BlurSourceHeight = 270;
    public const double BlurSigma = 8;

    public static string Build(int photoCount, double secondsPerPhoto, TransitionStyle transition, Random random)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;

        for (int i = 0; i < photoCount; i++)
        {
            sb.AppendFormat(ci,
                "[{0}:v]split=2[src{0}a][src{0}b];" +
                "[src{0}a]scale={5}:{6}:force_original_aspect_ratio=increase,crop={5}:{6},gblur=sigma={3},scale={1}:{2},setsar=1[bg{0}];" +
                "[src{0}b]scale={1}:{2}:force_original_aspect_ratio=decrease,setsar=1[fg{0}];" +
                "[bg{0}][fg{0}]overlay=(W-w)/2:(H-h)/2,fps={4},format=yuv420p,setpts=PTS-STARTPTS[seg{0}];",
                i,
                OutputWidth,
                OutputHeight,
                BlurSigma.ToString(ci),
                FrameRate,
                BlurSourceWidth,
                BlurSourceHeight);
        }

        if (photoCount == 1)
        {
            sb.Append("[seg0]copy[vout]");
            return sb.ToString();
        }

        // Chain xfade transitions. Each xfade's offset is where the transition begins in the
        // accumulated stream's timeline. Segment i contributes (S - T) of new visible time after
        // the transition before it, so the i-th transition begins at i*(S - T).
        string prev = "seg0";
        for (int i = 1; i < photoCount; i++)
        {
            string next = $"seg{i}";
            string outLabel = (i == photoCount - 1) ? "vout" : $"x{i}";
            double offset = i * (secondsPerPhoto - TransitionSeconds);
            if (offset < 0) offset = 0;
            string mode = XFadeMode(transition, i, random);
            sb.AppendFormat(ci,
                "[{0}][{1}]xfade=transition={2}:duration={3}:offset={4}[{5}];",
                prev, next, mode, TransitionSeconds.ToString(ci), offset.ToString(ci), outLabel);
            prev = outLabel;
        }

        if (sb[sb.Length - 1] == ';')
            sb.Length -= 1;

        return sb.ToString();
    }

    private static readonly string[] RandomPool = { "fade", "smoothleft", "slideleft", "dissolve", "zoomin" };

    private static string XFadeMode(TransitionStyle style, int transitionIndex, Random random) => style switch
    {
        TransitionStyle.Fade => "fade",
        TransitionStyle.Smooth => "smoothleft",
        TransitionStyle.Push => "slideleft",
        TransitionStyle.Dissolve => "dissolve",
        TransitionStyle.Zoom => "zoomin",
        TransitionStyle.Random => RandomPool[random.Next(RandomPool.Length)],
        _ => "fade"
    };
}
