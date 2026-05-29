using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Rendering;
using ImageMagick;

namespace EasyPhotoShow.Core.Tests;

public class TitleCardGeneratorTests
{
    [Fact]
    public void Generate_BlackBackground_ProducesValidJpeg()
    {
        var path = TempJpegPath();
        try
        {
            var slide = new TextCardSlide { Text = "In Loving Memory", Background = CardBackground.Black };
            TitleCardGenerator.Generate(slide, path);

            Assert.True(File.Exists(path));
            using var img = new MagickImage(path);
            Assert.Equal((uint)TitleCardGenerator.Width, img.Width);
            Assert.Equal((uint)TitleCardGenerator.Height, img.Height);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Generate_WhiteBackground_ProducesValidJpeg()
    {
        var path = TempJpegPath();
        try
        {
            var slide = new TextCardSlide { Text = "Thank You for Celebrating With Us", Background = CardBackground.White };
            TitleCardGenerator.Generate(slide, path);

            Assert.True(File.Exists(path));
            using var img = new MagickImage(path);
            Assert.Equal((uint)TitleCardGenerator.Width, img.Width);
            Assert.Equal((uint)TitleCardGenerator.Height, img.Height);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Generate_MaxLengthText_DoesNotThrow()
    {
        // Exactly the 120-char ceiling, with word boundaries to exercise wrapping + shrink.
        var text = "Celebrating a wonderful life filled with love laughter and cherished memories shared among family and our dearest friend";
        Assert.Equal(TextCardSlide.MaxLength, text.Length);

        foreach (var background in new[] { CardBackground.Black, CardBackground.White })
        {
            var path = TempJpegPath();
            try
            {
                TitleCardGenerator.Generate(new TextCardSlide { Text = text, Background = background }, path);
                Assert.True(File.Exists(path));
                using var img = new MagickImage(path);
                Assert.Equal((uint)TitleCardGenerator.Width, img.Width);
                Assert.Equal((uint)TitleCardGenerator.Height, img.Height);
            }
            finally { Cleanup(path); }
        }
    }

    [Fact]
    public void Generate_EmptyText_DoesNotThrow()
    {
        var path = TempJpegPath();
        try
        {
            TitleCardGenerator.Generate(new TextCardSlide { Text = "", Background = CardBackground.Black }, path);
            Assert.True(File.Exists(path));
            using var img = new MagickImage(path);
            Assert.Equal((uint)TitleCardGenerator.Width, img.Width);
            Assert.Equal((uint)TitleCardGenerator.Height, img.Height);
        }
        finally { Cleanup(path); }
    }

    private static string TempJpegPath() =>
        Path.Combine(Path.GetTempPath(), $"titlecard_{Guid.NewGuid():N}.jpg");

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
