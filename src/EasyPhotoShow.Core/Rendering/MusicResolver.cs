using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Rendering;

public static class MusicResolver
{
    public static string? ResolveMp3Path(MusicChoice choice)
    {
        switch (choice.Preset)
        {
            case MusicPreset.None:
                return null;
            case MusicPreset.Custom:
                return choice.CustomMp3Path is not null && File.Exists(choice.CustomMp3Path)
                    ? choice.CustomMp3Path : null;
            default:
                var name = choice.Preset.ToString().ToLowerInvariant();
                var candidate = Path.Combine(AppContext.BaseDirectory, "Assets", "Music", $"{name}.mp3");
                return File.Exists(candidate) ? candidate : null;
        }
    }
}
