namespace EasyPhotoShow.Core.Models;

public enum MusicPreset
{
    None,
    Celebration,
    Peaceful,
    Reflective,
    Custom
}

public sealed class MusicChoice
{
    public required MusicPreset Preset { get; init; }
    public string? CustomMp3Path { get; init; }

    public static MusicChoice None() => new() { Preset = MusicPreset.None };
}
