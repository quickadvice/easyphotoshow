using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Scanning;

public static class SupportedFormats
{
    public static bool TryClassify(string path, out PhotoFormat format)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".jpg":
            case ".jpeg":
                format = PhotoFormat.Jpeg;
                return true;
            case ".png":
                format = PhotoFormat.Png;
                return true;
            case ".heic":
            case ".heif":
                format = PhotoFormat.Heic;
                return true;
            default:
                format = default;
                return false;
        }
    }
}
