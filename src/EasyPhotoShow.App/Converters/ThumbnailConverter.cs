using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace EasyPhotoShow.App.Converters;

// Loads a thumbnail from a photo path, honoring EXIF orientation.
// Uses WPF native BitmapImage for JPG/PNG (fast), Magick.NET for HEIC/HEIF.
public sealed class ThumbnailConverter : IValueConverter
{
    public int DecodeWidth { get; set; } = 240;

    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext is ".heic" or ".heif")
                return LoadWithMagick(path);
            return LoadWithWpf(path);
        }
        catch
        {
            try { return LoadWithMagick(path); } catch { return null; }
        }
    }

    private BitmapSource LoadWithWpf(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = DecodeWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        // Apply EXIF rotation manually since WPF doesn't auto-rotate JPEGs for display by default
        return ApplyExifRotation(bmp, path);
    }

    private static BitmapSource ApplyExifRotation(BitmapSource source, string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var meta = frame.Metadata as BitmapMetadata;
            int orientation = 1;
            if (meta is not null && meta.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var raw = meta.GetQuery("/app1/ifd/{ushort=274}");
                if (raw is ushort u) orientation = u;
            }
            if (orientation == 1) return source;

            var transform = orientation switch
            {
                2 => (Transform)new ScaleTransform(-1, 1),
                3 => new RotateTransform(180),
                4 => new ScaleTransform(1, -1),
                5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
                6 => new RotateTransform(90),
                7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
                8 => new RotateTransform(270),
                _ => null!
            };
            if (transform is null) return source;
            var transformed = new TransformedBitmap(source, transform);
            transformed.Freeze();
            return transformed;
        }
        catch { return source; }
    }

    private BitmapSource LoadWithMagick(string path)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        if (image.Width > DecodeWidth)
        {
            var size = new MagickGeometry((uint)DecodeWidth, (uint)DecodeWidth) { Greater = true };
            image.Resize(size);
        }
        image.Format = MagickFormat.Bgra;
        var pixels = image.GetPixelsUnsafe().ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Failed to decode pixels.");
        var bmp = BitmapSource.Create(
            (int)image.Width, (int)image.Height,
            96, 96,
            PixelFormats.Bgra32, null,
            pixels, (int)image.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
