using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace EasyPhotoShow.App.Imaging;

// Decode-only helper that returns a frozen BitmapSource. Safe to call from any thread.
// Used by DuplicatePhotoItem.BeginLoadThumbnail (background) and by the legacy
// ThumbnailConverter (UI thread, synchronous — for any code that hasn't migrated yet).
// EXIF orientation is honored — see comments inline.
internal static class ThumbnailLoader
{
    public static BitmapSource? Load(string path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext is ".heic" or ".heif"
                ? LoadWithMagick(path, decodeWidth)
                : LoadWithWpf(path, decodeWidth);
        }
        catch
        {
            // Fall back to Magick for anything that WPF chokes on (corrupt JPEG, weird color
            // profile, etc.). Returns null only if even Magick can't decode.
            try { return LoadWithMagick(path, decodeWidth); } catch { return null; }
        }
    }

    private static BitmapSource LoadWithWpf(string path, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
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

    private static BitmapSource LoadWithMagick(string path, int decodeWidth)
    {
        using var image = new MagickImage(path);
        image.AutoOrient();
        if (image.Width > decodeWidth)
        {
            var size = new MagickGeometry((uint)decodeWidth, (uint)decodeWidth) { Greater = true };
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
}
