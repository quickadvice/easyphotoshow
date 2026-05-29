namespace EasyPhotoShow.Core.Models;

// Optional opening / closing slide content. An opener or closer is either a user-chosen
// photo (CustomImageSlide) or an app-generated text card (TextCardSlide). Null means the
// bookend is off. Session-only state — never persisted to disk.
public abstract class SlideContent { }

public sealed class CustomImageSlide : SlideContent
{
    public string ImagePath { get; init; } = string.Empty;
}

public sealed class TextCardSlide : SlideContent
{
    public const int MaxLength = 120;

    public string Text { get; init; } = string.Empty;
    public CardBackground Background { get; init; } = CardBackground.Black;
}

public enum CardBackground { Black, White }
