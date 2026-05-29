namespace EasyPhotoShow.Core.Rendering;

public enum RenderFailureKind
{
    Unknown,
    DiskFull,
    OutputUnwritable,
    SourceUnavailable,
    EncoderFailure
}

public sealed class RenderException : Exception
{
    public RenderFailureKind Kind { get; }
    public string LogPath { get; }

    public RenderException(RenderFailureKind kind, string userMessage, string logPath)
        : base(userMessage)
    {
        Kind = kind;
        LogPath = logPath;
    }
}
