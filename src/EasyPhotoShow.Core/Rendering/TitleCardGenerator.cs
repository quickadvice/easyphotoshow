using EasyPhotoShow.Core.Models;
using ImageMagick;

namespace EasyPhotoShow.Core.Rendering;

// Renders a TextCardSlide to a 1920×1080 JPEG (quality 92, matching StagingNormalizer) so
// an opener/closer text card drops into the render pipeline as an ordinary staged file.
// Synchronous by design — the caller (RenderJob) runs it on the background render task,
// before Stage 1, into the temp staging directory.
public static class TitleCardGenerator
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const int JpegQuality = 92;

    // Keep text well clear of the edges (spec: 120px horizontal margin; same vertically).
    private const int HorizontalMargin = 120;
    private const int VerticalMargin = 120;

    private const double StartFontSize = 72;
    private const double MinFontSize = 16;
    private const double FontStep = 2;
    private const double LineSpacingFactor = 1.25;

    public static void Generate(TextCardSlide slide, string outputPath)
    {
        var background = slide.Background == CardBackground.White ? MagickColors.White : MagickColors.Black;
        // Auto-contrast: white text on black, black text on white.
        var textColor = slide.Background == CardBackground.White ? MagickColors.Black : MagickColors.White;

        using var image = new MagickImage(background, (uint)Width, (uint)Height);
        image.ColorSpace = ColorSpace.sRGB;

        var text = (slide.Text ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            image.Settings.Font = ResolveFont();
            image.Settings.FillColor = textColor;
            image.Settings.TextGravity = Gravity.Center;

            double usableWidth = Width - 2.0 * HorizontalMargin;
            double usableHeight = Height - 2.0 * VerticalMargin;

            var (fontSize, wrapped) = FitText(image, text, usableWidth, usableHeight);
            image.Settings.FontPointsize = fontSize;
            image.Annotate(wrapped, Gravity.Center);
        }

        image.Quality = (uint)JpegQuality;
        image.Format = MagickFormat.Jpeg;
        image.Write(outputPath);
    }

    // Shrinks the font from 72pt until the word-wrapped block fits the usable box, then
    // returns the chosen size and the newline-joined text for Annotate to center.
    private static (double FontSize, string Wrapped) FitText(
        MagickImage image, string text, double usableWidth, double usableHeight)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (StartFontSize, text);

        for (double size = StartFontSize; size >= MinFontSize; size -= FontStep)
        {
            image.Settings.FontPointsize = size;
            var lines = WrapToWidth(image, words, usableWidth);
            double blockHeight = lines.Count * LineHeight(image, size);
            bool everyLineFits = lines.TrueForAll(line => MeasureWidth(image, line) <= usableWidth);
            if (everyLineFits && blockHeight <= usableHeight)
                return (size, string.Join('\n', lines));
        }

        // Floor: render at the minimum size with the best wrap we can manage. A pathological
        // single 120-char word can still overflow — acceptable, and it never throws.
        image.Settings.FontPointsize = MinFontSize;
        return (MinFontSize, string.Join('\n', WrapToWidth(image, words, usableWidth)));
    }

    // Greedy word wrap at the current font size: pack words onto a line until the next word
    // would exceed usableWidth, then break.
    private static List<string> WrapToWidth(MagickImage image, string[] words, double usableWidth)
    {
        var lines = new List<string>();
        var current = words[0];
        for (int i = 1; i < words.Length; i++)
        {
            var candidate = current + " " + words[i];
            if (MeasureWidth(image, candidate) <= usableWidth)
                current = candidate;
            else
            {
                lines.Add(current);
                current = words[i];
            }
        }
        lines.Add(current);
        return lines;
    }

    private static double MeasureWidth(MagickImage image, string line)
    {
        // FontTypeMetrics uses the image's current Settings.Font + FontPointsize. Falls back
        // to a rough estimate if metrics are unavailable (e.g. font failed to resolve) so the
        // generator degrades gracefully rather than throwing.
        var metrics = image.FontTypeMetrics(line);
        return metrics?.TextWidth ?? line.Length * image.Settings.FontPointsize * 0.5;
    }

    private static double LineHeight(MagickImage image, double size)
    {
        var metrics = image.FontTypeMetrics("Ag");
        double textHeight = metrics?.TextHeight ?? size * 1.2;
        return textHeight * LineSpacingFactor;
    }

    // Prefer a concrete font file (most reliable in Magick.NET, which doesn't bundle fonts):
    // Segoe UI (Windows default), then Arial. Falls back to the "Arial" family name if neither
    // file is present (e.g. non-Windows test host).
    private static string ResolveFont()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var file in new[] { "segoeui.ttf", "arial.ttf" })
        {
            var path = Path.Combine(fontsDir, file);
            if (File.Exists(path)) return path;
        }
        return "Arial";
    }
}
