using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Ordering;
using EasyPhotoShow.Core.Rendering;

namespace EasyPhotoShow.App.ViewModels;

public partial class RenderingViewModel : ObservableObject
{
    [ObservableProperty] private string stageLabel = "Preparing your photos...";
    [ObservableProperty] private double progressFraction;
    [ObservableProperty] private string phaseProgressText = "";
    [ObservableProperty] private string overallProgressText = "Overall progress: 0%";
    // Two-line ETA display. EtaPrimary is the headline; EtaSubtitle is the
    // confidence note that explains where the number comes from.
    [ObservableProperty] private string etaPrimary = "Calculating...";
    [ObservableProperty] private string etaSubtitle = "Updates as more photos are processed";
    [ObservableProperty] private bool isStageFinalizing;
    [ObservableProperty] private bool isRendering = true;

    private readonly SlideshowSettings _settings;
    private readonly CancellationTokenSource _cts = new();

    // ETA throttle: only refresh the displayed estimate every N photos (vs every
    // progress event, which fires many times per chunk). -1 = no estimate seen yet.
    private int _lastEtaPhotosProcessed = -1;
    private const int EtaRefreshEveryNPhotos = 10;

    public RenderingViewModel(SlideshowSettings settings)
    {
        _settings = settings;
        _ = RunAsync();
    }

    partial void OnProgressFractionChanged(double value)
    {
        var pct = Math.Clamp((int)Math.Round(value * 100), 0, 100);
        OverallProgressText = $"Overall progress: {pct}%";
    }

    private async Task RunAsync()
    {
        try
        {
            if (!FFmpegEnvironment.IsAvailable())
            {
                MessageBox.Show(
                    "EasyPhotoShow couldn't find the rendering engine. Please reinstall the application or place ffmpeg.exe into a 'tools/ffmpeg/' folder beside the program.",
                    "EasyPhotoShow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                App.Navigation.NavigateTo(new SlideshowCreationViewModel());
                return;
            }

            var orderedPhotos = _settings.Ordering == PhotoOrdering.BestMix
                ? BestMixOrderer.Order(_settings.Photos)
                : _settings.Photos;

            var progress = new Progress<RenderProgress>(p =>
            {
                StageLabel = StageLabelFor(p.Stage);
                PhaseProgressText = PhaseProgressFor(p.Stage, p.PhotosProcessed, p.PhotosTotal);
                ProgressFraction = p.FractionComplete;
                IsStageFinalizing = p.Stage == RenderStage.FinalizingSlideshow
                    || p.Stage == RenderStage.AddingMusic;
                UpdateEta(p);
            });

            var job = new RenderJob();
            await job.RunAsync(_settings, orderedPhotos, progress, _cts.Token);

            // Ensure the user sees the bar reach 100% and the wording resolve cleanly
            // before we navigate away.
            ProgressFraction = 1.0;
            StageLabel = "Almost done...";
            PhaseProgressText = "Slideshow ready";
            EtaPrimary = "";
            EtaSubtitle = "";
            IsStageFinalizing = false;
            await Task.Delay(550);

            IsRendering = false;
            App.Session.FinishedOutputPath = _settings.OutputPath;
            App.Navigation.NavigateTo(new CompletionViewModel(_settings.OutputPath));
        }
        catch (OperationCanceledException)
        {
            IsRendering = false;
            App.Navigation.NavigateTo(new SlideshowCreationViewModel());
        }
        catch (RenderException ex)
        {
            IsRendering = false;
            MessageBox.Show(ex.Message + $"\n\nA log was saved to:\n{ex.LogPath}",
                "EasyPhotoShow", MessageBoxButton.OK, MessageBoxImage.Information);
            App.Navigation.NavigateTo(new SlideshowCreationViewModel());
        }
        catch (Exception)
        {
            IsRendering = false;
            MessageBox.Show("Something unexpected happened. Please try again.", "EasyPhotoShow", MessageBoxButton.OK, MessageBoxImage.Information);
            App.Navigation.NavigateTo(new SlideshowCreationViewModel());
        }
    }

    private static string StageLabelFor(RenderStage stage) => stage switch
    {
        RenderStage.PreparingPhotos => "Preparing your photos...",
        RenderStage.CreatingSlideshow => "Creating video and adding transitions...",
        RenderStage.AddingMusic => "Adding music...",
        // "Putting it all together..." instead of "Saving your slideshow..." — more active
        // wording for a stage that can take 1-2 minutes on long slideshows (faststart moov
        // rewrite is O(file size)). Pairs with the indeterminate progress pulse in the view.
        RenderStage.FinalizingSlideshow => "Putting it all together...",
        RenderStage.Complete => "Almost done...",
        _ => "Working..."
    };

    // Phase-specific wording. Different verbs across stages so the user can see
    // each photo-based pass is doing different work, not repeating the same task.
    private static string PhaseProgressFor(RenderStage stage, int processed, int total)
    {
        if (total <= 0) return "";
        int safeProcessed = Math.Clamp(processed, 0, total);
        bool isLast = safeProcessed >= total;

        return stage switch
        {
            RenderStage.PreparingPhotos => isLast
                ? "All photos prepared"
                : $"Preparing photo {safeProcessed} of {total}",
            RenderStage.CreatingSlideshow => isLast
                ? "All frames built"
                : $"Building frame {safeProcessed} of {total}",
            RenderStage.AddingMusic => "Photos and frames complete",
            RenderStage.FinalizingSlideshow => "Photos and frames complete",
            _ => ""
        };
    }

    // Two-line ETA update. The displayed number is a rolling estimate from Core's
    // sliding-window rate (~75-photo window) — not a cumulative average from t=0 — so
    // it reflects current processing speed rather than the early-render anomaly that
    // used to make the displayed time climb 6→11→16→17 min before settling.
    //
    // Suppression: Core returns null EstimatedTimeRemaining until both ≥5% of photos
    // AND ≥2 chunks have completed, so the first displayed estimate is computed on a
    // stable sample. Until then we show "Calculating..." with a soft subtitle.
    //
    // Throttle rule: once an ETA is visible, refresh the displayed estimate only every
    // EtaRefreshEveryNPhotos photos processed (not on every progress event) to avoid
    // UI churn from per-frame FFmpeg progress messages.
    //
    // Stage 3 (FinalizingSlideshow / AddingMusic) doesn't emit per-photo estimates;
    // when we hit those stages, clear the ETA so the indeterminate pulse + "Putting it
    // all together..." label become the focal point.
    private void UpdateEta(RenderProgress p)
    {
        bool isFinalizing = p.Stage == RenderStage.FinalizingSlideshow
            || p.Stage == RenderStage.AddingMusic;

        if (isFinalizing)
        {
            // Stage 3 doesn't produce ETAs. The indeterminate pulse + "Putting it all
            // together..." label carry the user through; leave the ETA empty so the
            // pulse is the focal point.
            EtaPrimary = "";
            EtaSubtitle = "";
            return;
        }

        if (!p.EstimatedTimeRemaining.HasValue)
        {
            // Core is still suppressing the estimate (under the 5% / 2-chunks gate).
            // Keep "Calculating..." displayed; assignment is idempotent so re-firing
            // this branch on every progress event doesn't cause UI churn.
            if (_lastEtaPhotosProcessed < 0)
            {
                EtaPrimary = "Calculating...";
                EtaSubtitle = "Updates as more photos are processed";
            }
            return;
        }

        // First ETA, or 10+ photos since last refresh.
        bool firstEstimate = _lastEtaPhotosProcessed < 0;
        bool hitRefreshGate = p.PhotosProcessed - _lastEtaPhotosProcessed >= EtaRefreshEveryNPhotos;
        if (!firstEstimate && !hitRefreshGate) return;

        _lastEtaPhotosProcessed = p.PhotosProcessed;
        EtaPrimary = "Estimated time remaining: " + EtaTail(p.EstimatedTimeRemaining.Value);
        EtaSubtitle = "Based on recent processing speed";
    }

    // Returns just the tail of the ETA sentence — "X minutes", "1 minute", "Almost done".
    // Plural-aware. Round-to-30s already applied upstream in RenderJob.
    private static string EtaTail(TimeSpan t)
    {
        if (t.TotalSeconds < 30) return "Almost done";
        var totalMinutes = t.TotalMinutes;
        if (totalMinutes < 1) return "1 minute";
        int minutes = (int)Math.Ceiling(totalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsRendering) return;
        IsRendering = false;
        _cts.Cancel();
    }
}
