using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPhotoShow.App.Imaging;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.App.ViewModels;

public partial class DuplicatePhotoItem : ObservableObject
{
    private const int ThumbnailDecodeWidth = 240;
    private int _loadStarted;  // 0 = not started, 1 = started; Interlocked for thread safety

    public required Photo Photo { get; init; }
    public required bool IsRecommended { get; init; }
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private BitmapSource? thumbnail;
    public string DisplayName => System.IO.Path.GetFileName(Photo.Path);
    public string Dimensions => $"{Photo.VisualWidth} × {Photo.VisualHeight}";

    // Kicks off a background decode and marshals the result back to the UI thread.
    // Idempotent — safe to call repeatedly; the actual decode only runs once.
    // The Image binding starts out empty (Thumbnail = null) and updates when the
    // load completes, so the UI thread is never blocked by the decode.
    public void BeginLoadThumbnail()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) == 1) return;
        var path = Photo.Path;
        _ = Task.Run(() =>
        {
            var bmp = ThumbnailLoader.Load(path, ThumbnailDecodeWidth);
            if (bmp is null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(() => Thumbnail = bmp);
        });
    }
}

public partial class DuplicateGroupItem : ObservableObject
{
    public required IReadOnlyList<DuplicatePhotoItem> Photos { get; init; }
}

public partial class DuplicateReviewViewModel : ObservableObject
{
    public const int GroupsPerPage = 20;

    private readonly List<DuplicateGroupItem> _allGroups;
    private readonly IReadOnlyList<Photo> _allPhotos;

    public ObservableCollection<DuplicateGroupItem> CurrentPageGroups { get; } = new();

    [ObservableProperty] private int currentPage;
    [ObservableProperty] private int totalPages;
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string pageText = "";
    // Surfaced only if Phase 2 auto-resolved at least one exact copy during the scan.
    // The view binds Visibility to non-null so an empty/zero state shows nothing.
    [ObservableProperty] private string? exactCopyNote;
    // Button label for "Use Recommended Choices". Briefly swaps to a confirmation
    // message after the command runs so the user gets visible feedback even when no
    // checkboxes change (which is the common case — they're already recommended by default).
    [ObservableProperty] private string useRecommendedChoicesLabel = "Use Recommended Choices";
    private const string DefaultUseRecommendedLabel = "Use Recommended Choices";
    private const string AppliedUseRecommendedLabel = "Recommendations applied ✓";
    private const int ConfirmationDurationMs = 1500;
    private int _useRecommendedFeedbackToken;

    public DuplicateReviewViewModel(IReadOnlyList<DuplicateGroup> groups, IReadOnlyList<Photo> allPhotos)
    {
        _allPhotos = allPhotos;
        _allGroups = groups.Select(g => new DuplicateGroupItem
        {
            Photos = g.Photos.Select(p => new DuplicatePhotoItem
            {
                Photo = p,
                IsRecommended = ReferenceEquals(p, g.Recommended)
                    || string.Equals(p.Path, g.Recommended.Path, StringComparison.OrdinalIgnoreCase),
                IsSelected = string.Equals(p.Path, g.Recommended.Path, StringComparison.OrdinalIgnoreCase)
            }).ToList()
        }).ToList();

        TotalPages = Math.Max(1, (int)Math.Ceiling((double)_allGroups.Count / GroupsPerPage));
        SummaryText = $"Found {_allGroups.Count} groups of similar-looking photos.";

        // Pull the auto-resolved exact-copy count from the session. Set by ScanningViewModel
        // after ExactGroupAutoResolver runs. Only render the note when count > 0 — silent
        // when there were no exact copies (per Decision 5).
        int exactCount = App.Session.ExactCopyCount;
        if (exactCount > 0)
        {
            exactCopyNote = $"{exactCount} exact duplicate " +
                (exactCount == 1 ? "file" : "files") +
                " were also set aside safely — your originals are untouched.";
        }

        LoadPage(0);
    }

    private void LoadPage(int page)
    {
        CurrentPage = Math.Clamp(page, 0, TotalPages - 1);
        CurrentPageGroups.Clear();
        var slice = _allGroups.Skip(CurrentPage * GroupsPerPage).Take(GroupsPerPage);
        foreach (var g in slice)
        {
            CurrentPageGroups.Add(g);
            // Kick off background decodes for everything on this page. Idempotent — if the
            // user pages forward then back, already-loaded thumbnails don't decode twice.
            foreach (var photo in g.Photos)
                photo.BeginLoadThumbnail();
        }
        PageText = $"Page {CurrentPage + 1} of {TotalPages}";
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrev() => CurrentPage > 0;
    private bool CanNext() => CurrentPage < TotalPages - 1;

    [RelayCommand(CanExecute = nameof(CanPrev))]
    private void PrevPage() => LoadPage(CurrentPage - 1);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() => LoadPage(CurrentPage + 1);

    [RelayCommand]
    private async Task Continue()
    {
        var unselected = _allGroups
            .SelectMany(g => g.Photos)
            .Where(p => !p.IsSelected)
            .Select(p => p.Photo)
            .ToList();

        var droppedPaths = new HashSet<string>(
            unselected.Select(p => p.Path),
            StringComparer.OrdinalIgnoreCase);

        var photosForSlideshow = _allPhotos
            .Where(p => !droppedPaths.Contains(p.Path))
            .ToList();

        if (unselected.Count > 0)
        {
            var mover = new Core.Files.PotentialDuplicatesMover();
            await Task.Run(() => mover.Move(unselected, App.Session.SourceFolders));
        }

        App.Session.PhotosForSlideshow = photosForSlideshow;
        App.Navigation.NavigateTo(new SlideshowCreationViewModel());
    }

    [RelayCommand]
    private void SkipReview()
    {
        App.Session.PhotosForSlideshow = _allPhotos;
        App.Navigation.NavigateTo(new SlideshowCreationViewModel());
    }

    [RelayCommand]
    private async Task UseRecommendedChoices()
    {
        // Reset selections across EVERY page (not just the current page's visible items).
        // Verified by walking _allGroups, which holds the full set.
        foreach (var group in _allGroups)
            foreach (var photo in group.Photos)
                photo.IsSelected = photo.IsRecommended;

        // Transient label swap so the user sees feedback even when no checkbox state
        // changed (the default state already matches recommended, so without this the
        // button can appear to do nothing). Token guards against overlapping invocations
        // restoring the label early.
        int myToken = Interlocked.Increment(ref _useRecommendedFeedbackToken);
        UseRecommendedChoicesLabel = AppliedUseRecommendedLabel;
        await Task.Delay(ConfirmationDurationMs);
        if (_useRecommendedFeedbackToken == myToken)
            UseRecommendedChoicesLabel = DefaultUseRecommendedLabel;
    }
}
