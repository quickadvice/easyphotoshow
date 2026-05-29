using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Files;

public sealed class PotentialDuplicatesMover
{
    public const string FolderName = "PotentialDuplicates";

    public sealed class MoveReport
    {
        public List<Photo> Moved { get; } = new();
        public List<(Photo Photo, string Reason)> Failed { get; } = new();
    }

    public MoveReport Move(IEnumerable<Photo> unselected, IEnumerable<string> sourceFolders, CancellationToken ct = default)
    {
        var report = new MoveReport();
        var fallbackTarget = ResolveFallbackTarget(sourceFolders);

        foreach (var photo in unselected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sourceDir = Path.GetDirectoryName(photo.Path)
                    ?? throw new InvalidOperationException("Photo has no parent directory.");

                string targetDir;
                if (IsWritable(sourceDir))
                    targetDir = Path.Combine(sourceDir, FolderName);
                else if (fallbackTarget is not null)
                    targetDir = fallbackTarget;
                else
                    throw new IOException("No writable location available for PotentialDuplicates.");

                Directory.CreateDirectory(targetDir);
                var dest = ResolveCollisionFreeName(targetDir, Path.GetFileName(photo.Path));

                File.Move(photo.Path, dest);
                report.Moved.Add(photo);
            }
            catch (Exception ex)
            {
                report.Failed.Add((photo, ex.Message));
            }
        }
        return report;
    }

    private static string? ResolveFallbackTarget(IEnumerable<string> sourceFolders)
    {
        foreach (var folder in sourceFolders)
        {
            if (IsWritable(folder))
                return Path.Combine(folder, FolderName);
        }
        return null;
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, ".eps_write_probe_" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveCollisionFreeName(string targetDir, string fileName)
    {
        var dest = Path.Combine(targetDir, fileName);
        if (!File.Exists(dest)) return dest;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(targetDir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Too many collisions in PotentialDuplicates folder.");
    }
}
