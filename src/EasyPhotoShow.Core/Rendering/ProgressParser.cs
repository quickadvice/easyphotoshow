using System.Globalization;

namespace EasyPhotoShow.Core.Rendering;

internal sealed class ProgressParser
{
    public double OutTimeSeconds { get; private set; }
    public double Fps { get; private set; }
    public bool Finished { get; private set; }
    public int Frame { get; private set; }

    public void Feed(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        int eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line.Substring(0, eq).Trim();
        var val = line.Substring(eq + 1).Trim();
        switch (key)
        {
            case "out_time_us":
            case "out_time_ms":
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    OutTimeSeconds = us / 1_000_000.0;
                break;
            case "frame":
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
                    Frame = f;
                break;
            case "fps":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                    Fps = fps;
                break;
            case "progress":
                if (val == "end") Finished = true;
                break;
        }
    }
}
