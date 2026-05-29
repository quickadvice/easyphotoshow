using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPhotoShow.App.Session;
using EasyPhotoShow.Core.Models;
using Microsoft.Win32;

namespace EasyPhotoShow.App.ViewModels;

public partial class SlideshowCreationViewModel : ObservableObject
{
    private readonly IReadOnlyList<Photo> _photos;

    [ObservableProperty] private int photoCount;
    [ObservableProperty] private double secondsPerPhoto = 4.0;
    [ObservableProperty] private string estimatedRuntime = "";
    [ObservableProperty] private PhotoOrdering ordering = PhotoOrdering.BestMix;
    [ObservableProperty] private TransitionStyle transition = TransitionStyle.Fade;
    [ObservableProperty] private MusicPreset musicPreset = MusicPreset.None;
    [ObservableProperty] private string? customMusicPath;
    [ObservableProperty] private string? customMusicDisplay;
    [ObservableProperty] private string slideshowName = "MySlideshow";
    [ObservableProperty] private string saveFolder = "";
    [ObservableProperty] private string? trialWarning;

    // Quiet informational line at the action row when a chosen bookend image is also in the
    // main collection (silently removed from the slideshow). Null = no removal.
    [ObservableProperty] private string? slidesRemovalNote;

    // Optional opening / closing slide configuration. Each section preserves both its
    // Custom-Image and Text states independently (see SlideSectionViewModel).
    public SlideSectionViewModel Opener { get; } = new("Opening Slide", "Add opening slide");
    public SlideSectionViewModel Closer { get; } = new("Closing Slide", "Add closing slide");

    public IEnumerable<PhotoOrdering> OrderingOptions => Enum.GetValues<PhotoOrdering>();
    public IEnumerable<TransitionStyle> TransitionOptions => Enum.GetValues<TransitionStyle>();
    public IEnumerable<MusicPreset> MusicOptions => Enum.GetValues<MusicPreset>();

    // Drives the "Upload MP3" button's visibility — it only appears once the user picks
    // the Custom track option.
    public bool IsCustomMusicSelected => MusicPreset == MusicPreset.Custom;

    public SlideshowCreationViewModel()
    {
        _photos = App.Session.PhotosForSlideshow ?? Array.Empty<Photo>();
        PhotoCount = _photos.Count;
        SaveFolder = DefaultSaveFolder(App.Session.SourceFolders);
        SlideshowName = AvailableFilename(SaveFolder, DefaultBaseName(App.Session.SourceFolders));
        RecomputeRuntime();

        // Re-evaluate the "removed from the main slideshow" note whenever either section's
        // selection changes (enable/mode/image-path all matter).
        Opener.PropertyChanged += (_, _) => RecomputeSlidesRemovalNote();
        Closer.PropertyChanged += (_, _) => RecomputeSlidesRemovalNote();
    }

    // A bookend whose custom image is also one of the main photos gets silently removed from
    // the slideshow at Create() time; this note tells the user it happened. Text cards and
    // images that aren't in the collection don't trigger it.
    private void RecomputeSlidesRemovalNote()
    {
        bool openerInCollection = BookendImageInCollection(Opener);
        bool closerInCollection = BookendImageInCollection(Closer);

        SlidesRemovalNote = (openerInCollection, closerInCollection) switch
        {
            (true, true) => "Photos used as your opening and closing slides have been removed from the main slideshow.",
            (true, false) => "1 photo used as your opening slide has been removed from the main slideshow.",
            (false, true) => "1 photo used as your closing slide has been removed from the main slideshow.",
            _ => null
        };
    }

    private bool BookendImageInCollection(SlideSectionViewModel section)
    {
        if (section.BuildContent() is not CustomImageSlide image) return false;
        var target = NormalizePath(image.ImagePath);
        return _photos.Any(p => string.Equals(NormalizePath(p.Path), target, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string DefaultSaveFolder(IReadOnlyList<string> sourceFolders)
    {
        foreach (var folder in sourceFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                return folder;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private static string DefaultBaseName(IReadOnlyList<string> sourceFolders)
    {
        if (sourceFolders.Count == 1)
        {
            try
            {
                var name = new DirectoryInfo(sourceFolders[0]).Name;
                if (!string.IsNullOrWhiteSpace(name))
                    return $"{name} Slideshow";
            }
            catch { /* fall through */ }
        }
        return "My Slideshow";
    }

    // Walks "Base", "Base (2)", "Base (3)", ... until the .mp4 doesn't exist.
    // Caps at 999 to avoid runaway loops; the 1000th rendered slideshow into the same
    // folder is improbable but we degrade gracefully by returning the base name.
    private static string AvailableFilename(string folder, string baseName)
    {
        if (!Directory.Exists(folder)) return baseName;
        var firstPath = Path.Combine(folder, baseName + ".mp4");
        if (!File.Exists(firstPath)) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!File.Exists(Path.Combine(folder, candidate + ".mp4")))
                return candidate;
        }
        return baseName;
    }

    partial void OnSecondsPerPhotoChanged(double value)
    {
        if (value < 1) SecondsPerPhoto = 1;
        if (value > 20) SecondsPerPhoto = 20;
        RecomputeRuntime();
    }

    partial void OnMusicPresetChanged(MusicPreset value)
    {
        OnPropertyChanged(nameof(IsCustomMusicSelected));
        if (value != MusicPreset.Custom)
        {
            CustomMusicPath = null;
            CustomMusicDisplay = null;
        }
    }

    private void RecomputeRuntime()
    {
        var ts = TimeSpan.FromSeconds(PhotoCount * SecondsPerPhoto);
        EstimatedRuntime = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s"
            : ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s"
                : $"{ts.Seconds}s";

        TrialWarning = null;
        if (TrialLimits.ExceedsPhotoCap(PhotoCount))
            TrialWarning = $"This trial supports up to {TrialLimits.MaxPhotos} photos. Please reduce your selection or upgrade.";
        else if (TrialLimits.ExceedsDurationCap(PhotoCount, SecondsPerPhoto))
            TrialWarning = $"This trial supports slideshows up to {TrialLimits.MaxDuration.TotalMinutes:0} minutes. Please reduce the seconds per photo or upgrade.";

        CreateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ChooseCustomMusic()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Upload music (MP3)",
            Filter = "MP3 files (*.mp3)|*.mp3"
        };
        if (dlg.ShowDialog() == true)
        {
            CustomMusicPath = dlg.FileName;
            MusicPreset = MusicPreset.Custom;
            CustomMusicDisplay = Path.GetFileName(dlg.FileName);

            var duration = await Task.Run(() => Core.Rendering.MusicMetadataProbe.GetDuration(dlg.FileName));
            if (duration.HasValue)
            {
                var ts = duration.Value;
                var formatted = ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
                CustomMusicDisplay = $"{Path.GetFileName(dlg.FileName)}  ·  {formatted}";
            }
        }
    }

    [RelayCommand]
    private void ChooseSaveFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose where to save your slideshow",
            InitialDirectory = SaveFolder
        };
        if (dlg.ShowDialog() == true)
            SaveFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void Back()
    {
        App.Navigation.NavigateTo(new MainScreenViewModel());
    }

    private bool CanCreate() =>
        PhotoCount > 0
        && !string.IsNullOrWhiteSpace(SlideshowName)
        && !string.IsNullOrWhiteSpace(SaveFolder)
        && TrialWarning is null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        var sanitizedName = SanitizeFileName(SlideshowName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            MessageBox.Show("Please choose a name for your slideshow.", "EasyPhotoShow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(SaveFolder))
        {
            MessageBox.Show("That save folder is no longer available. Please choose another.", "EasyPhotoShow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Overwrite check — only matters after user-driven edits; the default name is
        // auto-incremented up front to avoid collisions on first open.
        var targetPath = Path.Combine(SaveFolder, sanitizedName + ".mp4");
        if (File.Exists(targetPath))
        {
            var overwrite = MessageBox.Show(
                $"A slideshow named \"{sanitizedName}.mp4\" already exists in that folder. Do you want to replace it?",
                "EasyPhotoShow",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (overwrite != MessageBoxResult.OK) return;
        }

        var (available, recommended) = EstimateSpace();
        if (available > 0 && available < recommended)
        {
            var proceed = MessageBox.Show(
                $"There may not be enough free space on the chosen drive. Recommended free space: {recommended / (1024L * 1024 * 1024)} GB. Continue anyway?",
                "EasyPhotoShow",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (proceed != MessageBoxResult.OK) return;
        }

        var music = MusicPreset == MusicPreset.Custom && CustomMusicPath is not null
            ? new MusicChoice { Preset = MusicPreset.Custom, CustomMp3Path = CustomMusicPath }
            : new MusicChoice { Preset = MusicPreset };

        var openerContent = Opener.BuildContent();
        var closerContent = Closer.BuildContent();

        // Silently drop any main photo that the user picked as a bookend image, so it doesn't
        // appear twice (once as the bookend, once in the body). Case-insensitive full-path match.
        var bookendImagePaths = new[] { openerContent, closerContent }
            .OfType<CustomImageSlide>()
            .Select(s => NormalizePath(s.ImagePath))
            .ToList();
        var photos = bookendImagePaths.Count == 0
            ? _photos
            : _photos.Where(p => !bookendImagePaths.Contains(NormalizePath(p.Path), StringComparer.OrdinalIgnoreCase)).ToList();

        var settings = new SlideshowSettings
        {
            Photos = photos,
            SecondsPerPhoto = SecondsPerPhoto,
            Ordering = Ordering,
            Transition = Transition,
            Music = music,
            SlideshowName = sanitizedName,
            SaveFolder = SaveFolder,
            OpenerSlide = openerContent,
            CloserSlide = closerContent
        };

        App.Session.OpenerSlide = openerContent;
        App.Session.CloserSlide = closerContent;
        App.Session.Settings = settings;
        App.Navigation.NavigateTo(new RenderingViewModel(settings));
    }

    private (long available, long recommended) EstimateSpace()
    {
        try
        {
            var root = Path.GetPathRoot(SaveFolder);
            if (string.IsNullOrEmpty(root)) return (0, 0);
            var drive = new DriveInfo(root);
            long minutes = (long)Math.Ceiling(PhotoCount * SecondsPerPhoto / 60.0);
            long recommended = Math.Max(2L * 1024 * 1024 * 1024, minutes * 50L * 1024 * 1024 * 3 / 2);
            return (drive.AvailableFreeSpace, recommended);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }
}

// One opening- or closing-slide section. Holds Custom-Image and Text states side by side so
// switching modes never discards the other mode's input; only the active mode is committed
// via BuildContent(). Pure view-state — no rendering, no file I/O beyond the file picker.
public partial class SlideSectionViewModel : ObservableObject
{
    private const int NearLimitThreshold = 100;

    // Matches Theme.xaml TextMutedColor (#94908A) — used for the preview placeholder text.
    private static readonly Brush PlaceholderBrush = FrozenBrush(Color.FromRgb(0x94, 0x90, 0x8A));

    public string SectionTitle { get; }
    public string EnableLabel { get; }

    public SlideSectionViewModel(string sectionTitle, string enableLabel)
    {
        SectionTitle = sectionTitle;
        EnableLabel = enableLabel;
    }

    [ObservableProperty] private bool isEnabled;
    // false = Custom Image, true = Text. Both states below persist across toggles of this.
    [ObservableProperty] private bool isTextMode;

    [ObservableProperty] private string? customImagePath;

    [ObservableProperty] private string text = "";
    [ObservableProperty] private CardBackground background = CardBackground.Black;

    // Mode helpers for the segmented control's active-pill triggers.
    public bool IsImageMode => !IsTextMode;

    public bool HasCustomImage => !string.IsNullOrEmpty(CustomImagePath);

    // Live text-card preview state.
    public int CharCount => Text?.Length ?? 0;
    public string CharCountText => $"{CharCount} / {TextCardSlide.MaxLength}";
    public bool IsNearLimit => CharCount >= NearLimitThreshold;
    public string PreviewText => string.IsNullOrEmpty(Text) ? "Your text will appear here" : Text;
    public Brush PreviewBackgroundBrush => Background == CardBackground.White ? Brushes.White : Brushes.Black;
    public Brush PreviewForegroundBrush => string.IsNullOrEmpty(Text)
        ? PlaceholderBrush
        : (Background == CardBackground.White ? Brushes.Black : Brushes.White);
    public bool IsBlackSelected => Background == CardBackground.Black;
    public bool IsWhiteSelected => Background == CardBackground.White;

    partial void OnIsTextModeChanged(bool value) => OnPropertyChanged(nameof(IsImageMode));
    partial void OnCustomImagePathChanged(string? value) => OnPropertyChanged(nameof(HasCustomImage));

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(CharCountText));
        OnPropertyChanged(nameof(IsNearLimit));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewForegroundBrush));
    }

    partial void OnBackgroundChanged(CardBackground value)
    {
        OnPropertyChanged(nameof(PreviewBackgroundBrush));
        OnPropertyChanged(nameof(PreviewForegroundBrush));
        OnPropertyChanged(nameof(IsBlackSelected));
        OnPropertyChanged(nameof(IsWhiteSelected));
    }

    [RelayCommand] private void UseImageMode() => IsTextMode = false;
    [RelayCommand] private void UseTextMode() => IsTextMode = true;
    [RelayCommand] private void SelectBlack() => Background = CardBackground.Black;
    [RelayCommand] private void SelectWhite() => Background = CardBackground.White;

    [RelayCommand]
    private void ChooseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a photo",
            Filter = "Image Files|*.jpg;*.jpeg;*.png;*.heic;*.heif"
        };
        if (dlg.ShowDialog() == true)
            CustomImagePath = dlg.FileName;
    }

    // The committed content for this section: null when off or incomplete; otherwise the
    // active mode's content only. The inactive mode's retained state is intentionally ignored.
    public SlideContent? BuildContent()
    {
        if (!IsEnabled) return null;
        if (IsTextMode)
        {
            var trimmed = (Text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : new TextCardSlide { Text = trimmed, Background = Background };
        }
        return string.IsNullOrEmpty(CustomImagePath) ? null : new CustomImageSlide { ImagePath = CustomImagePath };
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
