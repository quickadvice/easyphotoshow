using EasyPhotoShow.Core.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = System.IO.Directory;

namespace EasyPhotoShow.Core.Scanning;

public sealed class FolderScanner
{
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
    };

    public ScanResult Scan(IEnumerable<string> folders, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var photos = new List<Photo>();
        int unsupported = 0;
        int processed = 0;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*", WalkOptions))
            {
                ct.ThrowIfCancellationRequested();

                if (IsInsidePotentialDuplicatesFolder(path)) continue;
                if (!seen.Add(path)) continue;

                if (!SupportedFormats.TryClassify(path, out var format))
                {
                    if (IsImageLikeExtension(path))
                        unsupported++;
                    continue;
                }

                var photo = TryBuildPhoto(path, format);
                if (photo is null)
                {
                    unsupported++;
                    continue;
                }

                photos.Add(photo);
                processed++;
                if (processed % 25 == 0)
                    progress?.Report(processed);
            }
        }

        progress?.Report(processed);

        return new ScanResult
        {
            Photos = photos,
            UnsupportedFileCount = unsupported
        };
    }

    private static bool IsInsidePotentialDuplicatesFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "PotentialDuplicates", StringComparison.OrdinalIgnoreCase))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private static bool IsImageLikeExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gif" or ".bmp" or ".tiff" or ".tif" or ".webp" or ".raw"
            or ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" or ".orf";
    }

    private static Photo? TryBuildPhoto(string path, PhotoFormat format)
    {
        try
        {
            var info = new FileInfo(path);
            DateTime? captureTime = null;
            int width = 0;
            int height = 0;

            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);

                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd is not null)
                {
                    if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                        captureTime = dt;
                    if (width == 0 && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w))
                        width = w;
                    if (height == 0 && subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h))
                        height = h;
                }

                if (width == 0 || height == 0)
                {
                    foreach (var dir in directories)
                    {
                        foreach (var tag in dir.Tags)
                        {
                            if (width == 0 && tag.Name is "Image Width" && int.TryParse(tag.Description?.Split(' ')[0], out var w))
                                width = w;
                            if (height == 0 && tag.Name is "Image Height" && int.TryParse(tag.Description?.Split(' ')[0], out var h))
                                height = h;
                        }
                    }
                }
            }
            catch
            {
                // Metadata extractor can fail on unusual files; treat as unsupported only if we cannot get dimensions at all
            }

            if (width == 0 || height == 0)
                return null;

            captureTime ??= info.LastWriteTime;

            return new Photo
            {
                Path = path,
                FileSize = info.Length,
                Format = format,
                VisualWidth = width,
                VisualHeight = height,
                CaptureTime = captureTime
            };
        }
        catch
        {
            return null;
        }
    }
}
