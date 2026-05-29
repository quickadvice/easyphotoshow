using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyPhotoShow.Core.DuplicateDetection;
using EasyPhotoShow.Core.Models;
using EasyPhotoShow.Core.Scanning;

namespace EasyPhotoShow.App.ViewModels;

public partial class ScanningViewModel : ObservableObject
{
    private const int CompletionBeatMs = 1500;

    [ObservableProperty] private string statusText = "Reading photo details...";
    [ObservableProperty] private int photosFound;
    [ObservableProperty] private string? unsupportedNote;
    [ObservableProperty] private string? secondaryStatus;

    // Drives the scan-screen progress bar. Determinate during Phase 3 (dHash) where
    // total is known; indeterminate elsewhere (initial folder walk, SHA-256 which
    // typically finishes too fast to bother showing a determinate fraction).
    [ObservableProperty] private bool isProgressDeterminate;
    [ObservableProperty] private double progressFraction;

    // Two-track scan: ExactOnly groups are tallied into a running count as they're
    // discovered; visual groups go to the review screen at the end. The count is the only
    // exact-copy surface on the scan screen — thumbnails were removed because they never
    // finished decoding mid-scan and read as broken (blank grey boxes).
    [ObservableProperty] private int exactCopyCount;
    [ObservableProperty] private bool showExactCopySection;

    // Set during the 1.5 s completion beat between scan end and navigation. The view binds
    // visibility to non-null so the message only renders during the beat.
    [ObservableProperty] private string? completionMessage;

    private readonly bool _reviewDuplicatesAfter;
    private readonly CancellationTokenSource _cts = new();

    public ScanningViewModel(bool reviewDuplicatesAfter)
    {
        _reviewDuplicatesAfter = reviewDuplicatesAfter;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var scanner = new FolderScanner();
            var progress = new Progress<int>(c =>
            {
                PhotosFound = c;
                // Phase 1 — folder walk. Keep the headline string stable per approved spec;
                // photo count is surfaced via SecondaryStatus.
                StatusText = "Reading photo details...";
                SecondaryStatus = c > 0 ? $"Found {c} photos so far" : null;
            });

            // Phase 1 (Index) wall time for the [TIMING-SCAN] log. Detect times Phases 2–5;
            // the indexing walk + EXIF read happens here, before Detect is called, so we
            // measure it at the call site and stitch it onto the returned ScanTimings.
            var indexSw = Stopwatch.StartNew();
            var scan = await Task.Run(() => scanner.Scan(App.Session.SourceFolders, progress, _cts.Token), _cts.Token);
            indexSw.Stop();
            App.Session.Scan = scan;

            if (scan.UnsupportedFileCount > 0)
                UnsupportedNote = scan.UnsupportedFileCount == 1
                    ? "1 unsupported file was skipped."
                    : $"{scan.UnsupportedFileCount} unsupported files were skipped.";

            if (scan.Photos.Count == 0)
            {
                StatusText = "No supported photos were found in the selected folders.";
                await Task.Delay(2500);
                App.Navigation.NavigateTo(new MainScreenViewModel());
                return;
            }

            if (!_reviewDuplicatesAfter)
            {
                App.Session.DuplicateGroups = Array.Empty<DuplicateGroup>();
                App.Session.PhotosForSlideshow = scan.Photos;
                App.Session.ExactCopyCount = 0;
                App.Navigation.NavigateTo(new SlideshowCreationViewModel());
                return;
            }

            // Bridge between folder walk and duplicate detection. Cleared SecondaryStatus
            // because the per-photo "Found N photos so far" hint is stale here.
            StatusText = "Checking file names and sizes...";
            SecondaryStatus = null;
            PhotosFound = scan.Photos.Count;
            IsProgressDeterminate = false;

            var detector = new DuplicateDetector();
            var dupProgress = new Progress<DuplicateDetector.Progress>(p =>
            {
                // Detector emits stage labels straight through to the user. The strings
                // themselves are defined inside DuplicateDetector.cs to keep the source of
                // truth for user-facing wording close to the code that reports it. Approved
                // labels: "Checking file names and sizes", "Checking image resolution",
                // "Comparing similar-looking photos".
                if (!string.IsNullOrEmpty(p.Stage))
                    StatusText = p.Stage + "...";

                SecondaryStatus = p.PhotosTotal > 0
                    ? $"Checking photo {p.PhotosProcessed} of {p.PhotosTotal}"
                    : null;

                // Determinate bar during Phase 3 (dHash) where total is known and the work
                // is long enough to show meaningful movement. Phase 1 + 2 stay indeterminate.
                //
                // IMPORTANT: the stage string compared here MUST match the value set in
                // DuplicateDetector.ComputeDHashes. If you rename "Comparing similar-looking
                // photos" in one place, rename it here too — otherwise the bar silently
                // stays indeterminate for the entire Phase 3 and the user sees no progress
                // movement on the long stage of the scan.
                var isDhashPhase = string.Equals(p.Stage, "Comparing similar-looking photos", StringComparison.Ordinal);
                if (isDhashPhase && p.PhotosTotal > 0)
                {
                    IsProgressDeterminate = true;
                    ProgressFraction = Math.Clamp((double)p.PhotosProcessed / p.PhotosTotal, 0.0, 1.0);
                }
                else
                {
                    IsProgressDeterminate = false;
                }
            });

            // onExactGroupsReady fires from inside Detect on the Task.Run thread. Marshal
            // the ObservableProperty updates back to the UI thread.
            var dispatcher = Application.Current?.Dispatcher;
            Action<IReadOnlyList<DuplicateGroup>>? onExactReady = exactGroups =>
            {
                if (exactGroups.Count == 0) return;
                if (dispatcher is null || dispatcher.HasShutdownStarted)
                {
                    AppendExactGroups(exactGroups);
                }
                else
                {
                    dispatcher.Invoke(() => AppendExactGroups(exactGroups));
                }
            };

            var result = await Task.Run(
                () => detector.Detect(scan.Photos, dupProgress, onExactReady, _cts.Token),
                _cts.Token);

            // Stitch the Phase-1 (Index) measurement onto the timings Detect produced, then
            // write the [TIMING-SCAN] diagnostic log. Kept out of Core so DuplicateDetector
            // stays free of file-I/O side effects (mirrors LogAutoResolveResult below).
            result.Timings.Index = indexSw.Elapsed;
            result.Timings.PhotosScanned = scan.Photos.Count;
            result.Timings.UnsupportedCount = scan.UnsupportedFileCount;
            WriteScanTimingLog(result.Timings);

            // After Phase 3 finishes, briefly label the silent BuildGroups + classification work.
            StatusText = "Grouping possible duplicates...";
            SecondaryStatus = null;
            IsProgressDeterminate = false;

            // Re-attach dHashes to every Photo before anything downstream sees them.
            // Without this, BestMix's variety tiebreaker reads DHash = null and no-ops
            // (see DuplicateDetectionResult.cs comments for rationale).
            var enrichedPhotos = DuplicateDetector.AttachDHashes(scan.Photos, result.DHashByPath);

            // Auto-resolve ExactOnly groups. Move non-recommended exact copies to
            // PotentialDuplicates and remove them from the slideshow list. Run on a
            // background thread so the UI thread stays responsive during the move I/O.
            var autoResolveResult = await Task.Run(() => ExactGroupAutoResolver.Resolve(
                result.ExactOnlyGroups,
                enrichedPhotos,
                App.Session.SourceFolders,
                _cts.Token), _cts.Token);

            LogAutoResolveResult(autoResolveResult);

            // Session writes happen BEFORE navigation so the review/slideshow screens see
            // a fully-resolved state. ExactCopyCount may differ from the user-visible
            // running counter if some moves failed; the user-visible count is what was
            // promised, the session value is what was actually achieved.
            App.Session.DuplicateGroups = result.VisualGroups;
            App.Session.PhotosForSlideshow = autoResolveResult.RemainingPhotos;
            App.Session.ExactCopyCount = autoResolveResult.PhotosMoved;

            // 1.5 s completion beat. Choose the message based on what actually happened.
            // Per spec wording (see ARCHITECTURE OVERVIEW): three branches.
            int exactMoved = autoResolveResult.PhotosMoved;
            bool hasVisualGroups = result.VisualGroups.Count > 0;

            CompletionMessage = (exactMoved, hasVisualGroups) switch
            {
                ( > 0, true) =>
                    $"All done — {exactMoved} exact {(exactMoved == 1 ? "duplicate" : "duplicates")} handled. " +
                    "Now showing your similar photos...",
                ( > 0, false) =>
                    $"All done — {exactMoved} exact {(exactMoved == 1 ? "duplicate" : "duplicates")} handled.",
                (0, true) =>
                    "Checking complete. Preparing your review...",
                _ =>
                    "All done — no duplicates found." // (0, false) and any defensive negative case
            };

            // Hide the working-state UI while the completion message shows. The view binds
            // StatusText to a separate element so clearing it doesn't fight the message.
            StatusText = "";
            SecondaryStatus = null;

            await Task.Delay(CompletionBeatMs, _cts.Token);

            if (hasVisualGroups)
                // Must be RemainingPhotos, not enrichedPhotos — the review VM's Continue /
                // "Include All Photos" commands rebuild App.Session.PhotosForSlideshow from
                // this list. Passing enrichedPhotos re-introduces auto-moved files into the
                // slideshow and the render then crashes trying to open paths that no longer
                // exist on disk.
                App.Navigation.NavigateTo(new DuplicateReviewViewModel(result.VisualGroups, autoResolveResult.RemainingPhotos));
            else
                App.Navigation.NavigateTo(new SlideshowCreationViewModel());
        }
        catch (OperationCanceledException)
        {
            App.Navigation.NavigateTo(new MainScreenViewModel());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "EasyPhotoShow ran into a problem while reading your folders. Please try again.",
                "EasyPhotoShow",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            System.Diagnostics.Debug.WriteLine(ex);
            App.Navigation.NavigateTo(new MainScreenViewModel());
        }
    }

    // UI-thread only. Tallies newly-discovered exact groups into the running count and
    // reveals the count section. Counts duplicate files (group size minus the kept photo),
    // not groups — that's the number the user cares about ("N files handled").
    private void AppendExactGroups(IReadOnlyList<DuplicateGroup> groups)
    {
        foreach (var group in groups)
            ExactCopyCount += group.Photos.Count - 1;

        ShowExactCopySection = true;
    }

    // Diagnostic [TIMING-SCAN] block — one file per scan in the same log directory as the
    // render logs, with a distinct "scan_" prefix so the two never collide. Mirrors the
    // render log's column style (label padded to 27, seconds right-aligned in 9 cols, N2).
    // Counts in parentheses are as useful as the times for diagnosing large-batch behavior.
    private static void WriteScanTimingLog(ScanTimings t)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPhotoShow", "logs");
            Directory.CreateDirectory(logDir);
            var sessionId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(logDir, $"scan_{DateTime.Now:yyyyMMdd_HHmmss}_{sessionId[..8]}.log");

            var sb = new System.Text.StringBuilder();
            sb.Append($"[timing-scan] {"Index (walk + EXIF):",-27}{t.Index.TotalSeconds,9:N2} s   ({t.PhotosScanned:N0} photos, {t.UnsupportedCount:N0} unsupported)\n");
            sb.Append($"[timing-scan] {"Exact match (SHA-256):",-27}{t.ExactMatch.TotalSeconds,9:N2} s   ({t.FilesHashed:N0} files hashed)\n");
            sb.Append($"[timing-scan] {"dHash decode + compute:",-27}{t.DHashCompute.TotalSeconds,9:N2} s   ({t.PhotosHashed:N0} photos)\n");
            sb.Append($"[timing-scan] {"Pairwise comparison:",-27}{t.Pairwise.TotalSeconds,9:N2} s   ({t.PairsCompared:N0} pairs)\n");
            sb.Append($"[timing-scan] {"Group build (union-find):",-27}{t.GroupBuild.TotalSeconds,9:N2} s   ({t.GroupsFound:N0} groups found)\n");
            sb.Append($"[timing-scan] {"TOTAL:",-27}{t.Total.TotalSeconds,9:N2} s\n");
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* diagnostic only */ }
    }

    // Diagnostic write to the existing log directory. Doesn't share the render log path
    // (no render has started here) — use a dedicated autoresolve log for traceability.
    private static void LogAutoResolveResult(ExactGroupAutoResolver.Result result)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPhotoShow", "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"autoresolve_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[INFO] Auto-resolved {result.GroupsResolved} exact-copy groups " +
                          $"({result.PhotosMoved} photos moved to PotentialDuplicates)");
            if (result.MoveReport.Failed.Count > 0)
            {
                sb.AppendLine($"[INFO] {result.MoveReport.Failed.Count} moves failed — photos remain in slideshow:");
                foreach (var (photo, reason) in result.MoveReport.Failed)
                    sb.AppendLine($"  - {photo.Path}: {reason}");
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch { /* diagnostic only */ }
    }
}
