# EasyPhotoShow — Code Handoff

**Audience:** A developer (human or AI) picking up this project with no prior context. After reading this and the four design documents, you should be able to build, run, test, modify, and ship V1.

**Last updated:** 2026-05-28.

---

## 1. One-paragraph orientation

EasyPhotoShow is a calm Windows desktop app that turns a folder of photos into a polished MP4 slideshow. It is targeted at non-technical users handling emotionally important moments (memorial services, graduations, family events). The full product surface is described in `01_Product_Specification.md`. The app is **fully scaffolded and renders real MP4s end-to-end** as of the last-updated date. A handful of pre-launch items remain (see §10).

---

## 2. Repository layout

```
E:\Dev\EasyPhotoShow\
├── EasyPhotoShow.sln
├── Documentation/             ← you are here
│   ├── README.md
│   ├── 01_Product_Specification.md
│   ├── 02_DuplicateDetection_Design.md
│   ├── 03_BestMix_Design.md
│   ├── 04_ExportPipeline_Design.md
│   ├── 05_UX_UI_Specification.md
│   └── 06_Code_Handoff.md   ← this file
├── Icons/                     ← icon source assets (master PNG + .ico)
├── Errors/                    ← user-saved error logs and screenshots (gitignore candidate)
├── src/
│   ├── EasyPhotoShow.Core/    ← pure .NET 8 class library; domain logic + rendering
│   └── EasyPhotoShow.App/     ← WPF app (net8.0-windows); UI shell + view models
└── tests/
    └── EasyPhotoShow.Core.Tests/  ← xUnit
```

The solution has three projects. `Core` is pure .NET 8 (cross-platform-friendly in principle, even though the app is Windows-only — `Core` has no WPF or Windows-specific dependencies). `App` is WPF on `net8.0-windows` and references `Core`. Tests reference `Core` and use `InternalsVisibleTo` to test the internal `UnionFind`.

---

## 3. Architecture

```
┌────────────────────────────────────────────────────────────────┐
│ EasyPhotoShow.App (WPF, net8.0-windows)                        │
│                                                                │
│  MainWindow ──► NavigationService ──► current ViewModel        │
│                                                                │
│  ViewModels (CommunityToolkit.Mvvm)                            │
│   ├─ MainScreenViewModel                                       │
│   ├─ ScanningViewModel ─────► FolderScanner, DuplicateDetector │
│   ├─ DuplicateReviewViewModel ──► PotentialDuplicatesMover     │
│   ├─ SlideshowCreationViewModel ──► MusicMetadataProbe         │
│   ├─ RenderingViewModel ──► BestMixOrderer, RenderJob          │
│   ├─ CompletionViewModel                                       │
│   └─ PlaybackViewModel ──► WPF MediaElement                    │
│                                                                │
│  Views (XAML + minimal code-behind)                            │
│  Session state: SlideshowSession (in-memory, session-scoped)   │
│  Converters: Enum display, Thumbnail, FractionToWidth, etc.    │
└────────────┬───────────────────────────────────────────────────┘
             │ project reference
             ▼
┌────────────────────────────────────────────────────────────────┐
│ EasyPhotoShow.Core (pure .NET 8)                               │
│                                                                │
│  Models/         Photo, DuplicateGroup, SlideshowSettings,     │
│                  RenderProgress, MusicChoice, ...              │
│  Imaging/        NormalizedBitmapLoader (Magick.NET wrapper),  │
│                  DHash                                         │
│  Scanning/       FolderScanner, SupportedFormats               │
│  DuplicateDetection/  DuplicateDetector, UnionFind,            │
│                       RecommendedPhotoSelector                 │
│  Ordering/       EventClusterer, BestMixOrderer                │
│  Files/          PotentialDuplicatesMover                      │
│  Rendering/      RenderJob, StagingNormalizer, FilterGraph-    │
│                  Builder, EncoderProbe, FFmpegEnvironment,     │
│                  ProgressParser, MusicResolver,                │
│                  MusicMetadataProbe, RenderException           │
│                                                                │
│  External binaries: tools/ffmpeg/{ffmpeg,ffprobe}.exe          │
│   (copied to App's bin output via MSBuild <None>)              │
└────────────────────────────────────────────────────────────────┘
```

### Why this split

- **Core is testable without WPF.** All algorithmic and rendering code (the parts most likely to break silently) can be unit-tested.
- **Single decode path in Core.** Both UI thumbnails and dHash computation go through `NormalizedBitmapLoader` (Magick.NET). This is load-bearing — see §6.1 on orientation.
- **One direction of dependency.** App depends on Core. Core never depends on App or WPF. This keeps the architecture honest and tests fast.

### MVVM specifics

- `CommunityToolkit.Mvvm` provides `[ObservableProperty]` source-gen and `[RelayCommand]`. Most ViewModels use these attributes; the generated code lives in `obj/`.
- View-locator pattern: `App.xaml` declares `DataTemplate` mappings from ViewModel type to View UserControl. `MainWindow.ContentControl` shows `Navigation.Current` and WPF picks the right view automatically.
- `INavigationService` is a tiny single-page-at-a-time navigator (no back-stack in V1). Each ViewModel constructor receives whatever state it needs from `SlideshowSession` (singleton, in-memory only — V1 is session-based per spec §16).

---

## 4. File map — where each responsibility lives

### Core — Models (`src/EasyPhotoShow.Core/Models/`)
- `PhotoFormat.cs` — enum: Jpeg, Png, Heic
- `Photo.cs` — record: Path, FileSize, Format, VisualWidth, VisualHeight, CaptureTime, DHash?, Sha256?
- `NormalizedBitmap.cs` — RGBA pixel buffer + dimensions
- `ScanResult.cs` — Photos + UnsupportedFileCount
- `DuplicateGroup.cs` — Photos + Recommended
- `TransitionStyle.cs` — enum: Fade, Smooth, Push, Dissolve, Zoom, Random
- `MusicChoice.cs` — MusicPreset enum (None/Celebration/Peaceful/Reflective/Custom) + optional CustomMp3Path
- `SlideshowSettings.cs` — photos + seconds/photo + ordering + transition + music + name + folder; derives EstimatedRuntime + OutputPath
- `RenderProgress.cs` — RenderStage enum (PreparingPhotos, CreatingSlideshow, AddingMusic, FinalizingSlideshow, Complete) + FractionComplete + PhotosProcessed + PhotosTotal + ETA?

### Core — Imaging
- `NormalizedBitmapLoader.cs` — **the single decode path.** `Load()` returns a NormalizedBitmap; `WriteJpeg()` writes a normalized JPG for staging. Both apply EXIF rotation via `Magick.NET.AutoOrient()`, force sRGB + TrueColor, strip metadata on write.
- `DHash.cs` — 64-bit difference hash, 9×8 grayscale; static `HammingDistance()` via `BitOperations.PopCount`. `SimilarityThresholdBits = 8`.

### Core — Scanning
- `SupportedFormats.cs` — extension → PhotoFormat classification
- `FolderScanner.cs` — recursive walk with `EnumerationOptions.RecurseSubdirectories=true, IgnoreInaccessible=true`. Skips paths inside any `PotentialDuplicates/` subfolder. Counts unsupported image-like extensions for the "N skipped" message.

### Core — DuplicateDetection
- `DuplicateDetector.cs` — 3-phase pipeline (`Detect()`). Phase 2 (SHA-256) only hashes files with a size collision partner. Phase 3 (dHash) uses `Parallel.For`. Grouping via UnionFind across both relationships.
- `UnionFind.cs` — internal, path-compression + rank. Visible to tests via `InternalsVisibleTo`.
- `RecommendedPhotoSelector.cs` — 5-tier deterministic pick (resolution → file size → capture time → path length → alphabetical path).

### Core — Ordering
- `EventClusterer.cs` — 30-min EXIF-time gap clustering; alphabetical fallback when no capture times.
- `BestMixOrderer.cs` — Spacing-based: union-find clusters (dHash + capture time) distributed across slots at enforced T/N spacing. Deterministic. See `Documentation/03_BestMix_Design.md` §3.

### Core — Files
- `PotentialDuplicatesMover.cs` — `Move()` returns a `MoveReport` with Moved + Failed. Cross-drive fallback via `IsWritable()` probe. Collision-free naming via `_1`, `_2`, ... suffixes.

### Core — Rendering
- `FFmpegEnvironment.cs` — locates `ffmpeg.exe` and `ffprobe.exe` (bundled at `<bin>/tools/ffmpeg/` or on PATH). Throws `FFmpegMissingException` if not found.
- `EncoderProbe.cs` — picks the H.264 encoder via `-encoders` listing + 1-frame test. Cached. Ladder: NVENC → QSV → AMF → MediaFoundation → libopenh264.
- `StagingNormalizer.cs` — Stage 1. Writes normalized JPGs at quality 92, longer-side ≤ 2160 px. Parallel.
- `FilterGraphBuilder.cs` — builds the per-chunk FFmpeg filter_complex string. Cheap-blur (480×270 → upscale) + xfade chain. Don't touch the offset math without re-reading the inline comment.
- `RenderJob.cs` — orchestrates 3 stages. Chunked render (8 photos per FFmpeg invocation), lossless concat, audio added in concat pass.
- `ProgressParser.cs` — parses `-progress pipe:1` key=value lines.
- `MusicResolver.cs` — preset → `Assets/Music/{name}.mp3` resolution; custom MP3 passthrough.
- `MusicMetadataProbe.cs` — wraps ffprobe to read MP3 duration for UI display.
- `RenderException.cs` — RenderFailureKind enum + user-message-bearing exception.

### App — Shell + navigation
- `App.xaml` + `App.xaml.cs` — startup, initializes `Navigation` and `Session`, navigates to `MainScreenViewModel`. Declares `DataTemplate` view-locator mappings.
- `MainWindow.xaml` + `.xaml.cs` — chrome host. Hooks `Navigation.CurrentChanged` to swap content. `OnClosing` shows the close-during-render warning if a render is in flight.
- `Navigation/INavigationService.cs` + `NavigationService.cs` — single-current-page navigation with `CurrentChanged` event.
- `Session/SlideshowSession.cs` — in-memory state: source folders, scan result, duplicate groups, slideshow settings, finished output path.
- `Session/TrialLimits.cs` — 50 photos / 5 minutes constants + check methods.
- `app.manifest` — PerMonitorV2 DPI awareness + long-path support.

### App — ViewModels (`src/EasyPhotoShow.App/ViewModels/`)
- `MainScreenViewModel.cs` — folder list, Add Folder, "Review Similar Photos First" + "Use All Photos" navigation.
- `ScanningViewModel.cs` — runs FolderScanner then optionally DuplicateDetector. "Checking photo X of Y" status. Navigates to review or directly to setup.
- `DuplicateReviewViewModel.cs` — paginated groups; Use Recommended Choices; Continue (with file move); Include All Photos (no move).
- `SlideshowCreationViewModel.cs` — runtime computation, defaults from source folders, auto-increment filename, overwrite prompt, trial warning, Create handoff.
- `RenderingViewModel.cs` — wires `RenderJob` events to UI; stage-specific labels; ETA formatting (singular/plural fix); 100% hold before navigation; cancellation.
- `CompletionViewModel.cs` — View Slideshow / Open Folder / Done.
- `PlaybackViewModel.cs` — minimal — holds the file path; the view's code-behind drives MediaElement.

### App — Views (`src/EasyPhotoShow.App/Views/`)
All XAML + thin .xaml.cs files. PlaybackView has substantial code-behind because MediaElement is event-driven (no clean MVVM binding for play/pause state).

### App — Converters (`src/EasyPhotoShow.App/Converters/`)
- `Converters.cs` — InverseBoolToVisibility, BoolToVisibility, NullToVisibility, PercentConverter
- `EnumDisplayConverter.cs` — PascalCase → "Pascal Case" for dropdowns
- `ThumbnailConverter.cs` — path → BitmapSource (WPF fast-path for JPG/PNG with EXIF rotation; Magick.NET fallback for HEIC)
- `FractionToWidthConverter.cs` — IMultiValueConverter, used by the progress bar

### App — Styles + Assets
- `Styles/Theme.xaml` — colors, brushes, typography, button styles, Card style, custom ComboBox template
- `Assets/easyphotoshow.ico` — multi-resolution Windows app icon
- `Assets/easyphotoshow_256.png` — in-app branding image (main screen)
- `Assets/Music/` (gitignored / not present) — Celebration/Peaceful/Reflective MP3s when acquired
- `tools/ffmpeg/` (not committed) — ffmpeg.exe + ffprobe.exe

---

## 5. Build · Run · Test

### Prerequisites
- Windows 10 or 11
- **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8 --source winget`)
- FFmpeg LGPL Win64 binaries (see §5.2)

### Build

From the repo root (`E:\Dev\EasyPhotoShow`):

```pwsh
dotnet build EasyPhotoShow.sln
```

Expected: 0 warnings, 0 errors. The first build copies the 330 MB of FFmpeg binaries into `bin/Debug/net8.0-windows/tools/ffmpeg/` via the `<None CopyToOutputDirectory=PreserveNewest>` items in `EasyPhotoShow.App.csproj`. Subsequent builds skip the copy unless the binaries change.

### Run

```pwsh
dotnet run --project src/EasyPhotoShow.App
```

The `--project` flag means you can invoke from any directory. The WPF window appears with the EasyPhotoShow icon in the title bar and an icon-led main screen.

### Test

```pwsh
dotnet test EasyPhotoShow.sln --nologo
```

Expected: 34 passed, 0 failed. The integration test (`RenderJob_ProducesValidMp4_*`) does a real render and takes ~6-20 seconds depending on photo count; skips silently if FFmpeg isn't on PATH or in `tools/ffmpeg/`.

### Release publish

Not yet wired into CI. To produce a self-contained Windows .exe + dependencies:

```pwsh
dotnet publish src/EasyPhotoShow.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=false
```

This won't include FFmpeg binaries — they're `<None>` items with `CopyToOutputDirectory`, which `publish` honors, so they should land in the publish output. Verify before shipping.

---

## 5.2 Getting the FFmpeg binaries

The app requires an **LGPL** build of FFmpeg. Do NOT use a GPL build (would contaminate the commercial licensing story).

Recommended source: BtbN's CI-built LGPL Win64 release at https://github.com/BtbN/FFmpeg-Builds/releases/latest — file `ffmpeg-master-latest-win64-lgpl.zip` (~190 MB).

```pwsh
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-lgpl.zip'
$zip = Join-Path $env:TEMP "ffmpeg-lgpl.zip"
Invoke-WebRequest $url -OutFile $zip
Expand-Archive $zip -DestinationPath (Join-Path $env:TEMP "ffmpeg-extract") -Force
$src = (Get-ChildItem (Join-Path $env:TEMP "ffmpeg-extract") -Recurse -Filter bin -Directory | Select -First 1).FullName
Copy-Item (Join-Path $src ffmpeg.exe) "src\EasyPhotoShow.App\tools\ffmpeg\" -Force
Copy-Item (Join-Path $src ffprobe.exe) "src\EasyPhotoShow.App\tools\ffmpeg\" -Force
```

Each binary is ~165 MB. Do not commit them.

### What's in this LGPL build
- `libopenh264` (Cisco H.264, LGPL-compatible — the universal software fallback)
- Hardware H.264 encoders: `h264_nvenc`, `h264_qsv`, `h264_amf`, `h264_mf`
- All the filters we need: `xfade`, `gblur`, `scale`, `crop`, `overlay`, `split`, `fps`, `format`, `concat`

### What's NOT in this build
- `libx264` (GPL) — and we don't want it; `EncoderProbe.cs` is wired around `libopenh264`. Don't reintroduce libx264 without first switching to a GPL build, which would block commercial distribution.

---

## 6. Load-bearing decisions — read before changing

### 6.1 EXIF orientation is resolved at decode time, in one place

`NormalizedBitmapLoader.Load()` and `WriteJpeg()` both call `Magick.NET.AutoOrient()` and `Strip()` so:
- The returned bitmap dimensions are *visual* dimensions (a portrait iPhone shot reports width<height even though the sensor pixels are landscape)
- The staging JPG written for FFmpeg has no orientation EXIF tag

**Why this matters:** FFmpeg does NOT auto-apply EXIF orientation to still-image inputs. If you bypass `NormalizedBitmapLoader` and feed a raw JPG with orientation=6 to FFmpeg, the output video will show the photo sideways. The same bitmap is also used for the duplicate-review thumbnails and the dHash computation, so an orientation bug here breaks all four downstream consumers.

`OrientationTests.cs` exercises EXIF values 1, 3, 6, 8 + verifies WriteJpeg strips the tag. Don't disable these tests.

The single exception is `ThumbnailConverter` in App — it has a fast-path that uses WPF's `BitmapImage` for JPG/PNG (faster than Magick for small thumbnails) and applies EXIF rotation manually via `TransformedBitmap`. It falls back to Magick.NET for HEIC/HEIF.

### 6.2 LGPL FFmpeg + libopenh264 ladder

See §5.2 above and `04_ExportPipeline_Design.md` §5.4. **Do not reintroduce libx264** — it requires a GPL build of FFmpeg, which would block commercial paid distribution.

### 6.3 Chunked rendering (12 photos per FFmpeg invocation; see also 6.11 and 6.12)

A single complex filtergraph for the whole slideshow OOM'd on a 38-photo render. The current pipeline renders chunks of `PhotosPerChunk = 12` photos per FFmpeg invocation, then losslessly concatenates with `-c:v copy`. Tradeoff: a hard cut (no transition) appears at chunk boundaries every 12 photos.

`PhotosPerChunk = 12` is the empirically tuned value (see 6.12). The runtime memory guard in `RenderJob.RunAsync` falls back to 8 if `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 2 GB`. Do not change either constant without re-running the 50-photo cold-cache timing test.

### 6.4 Cheap blur (480×270 → upscale)

`FilterGraphBuilder` blurs the aspect-fill background at 480×270 then upscales to 1920×1080. The result is visually identical to blurring at full size (blur destroys detail anyway) but ~16× cheaper in memory and CPU. This is part of what made the OOM fix work; don't revert to full-resolution blur.

### 6.5 Color split: warm gold for actions, brand blue for system status

The icon is a blue rounded-square brand mark. The in-app UI uses warm gold (`#8B6F3F`) for primary actions to feel calm and on-brand with the warm photo theme. The **only** place brand blue appears in the in-app UI is the rendering progress bar fill (`#1E88E5`), where high contrast against the beige track was needed for visibility (see incident 4 below).

Don't introduce brand blue elsewhere without thinking about what it signals. Don't change the progress bar fill back to warm gold — the original implementation was hard to see.

### 6.6 Single decode path for thumbnails + dHash + rendering

Magick.NET is the canonical decoder. WPF imaging only appears in `ThumbnailConverter` as a fast-path optimization. Don't add another decode path without a strong reason — orientation bugs hide here.

### 6.7 No projects-save / sessions-only architecture

Per spec §16, V1 is explicitly session-based. `SlideshowSession` is in-memory only and is lost on app close. Don't add persistence without scope discussion — it implies project management, recent slideshows dashboard, etc.

### 6.8 Trial enforcement is always-on today

`TrialLimits` enforces 50-photo / 5-min caps unconditionally. There is no licensing/upgrade mechanism. Don't add a "Pro" check until the licensing system is designed (see §10 pre-launch items).

### 6.9 Determinism is required for Best Mix and the recommended-photo heuristic

Both must produce byte-identical results on the same input. Tests assert this. Don't introduce PRNG, clock reads, or thread-ordering dependencies.

### 6.10 ProgressBar's PART_Track + PART_Indicator template machinery is fragile

The first cut of the rendering progress bar used a custom `ProgressBar.Template` with only `PART_Indicator`. WPF's `SetProgressBarIndicatorLength()` silently no-ops if `PART_Track` is missing — so the fill never rendered. The current implementation bypasses `ProgressBar` entirely and uses two nested Borders with a `FractionToWidthConverter` (IMultiValueConverter) computing `fraction × track.ActualWidth`. Don't go back to templated ProgressBar.

### 6.11 Parallel chunk rendering (`MaxDegreeOfParallelism = 2`)

Chunks run two at a time via `Parallel.ForEachAsync(MaxDegreeOfParallelism = 2)` in `RenderJob.RenderChunksAsync`. Progress is aggregated via a `chunkFractions[]` array summed under `aggregateLock` — never last-reporter-wins. `LogLock` serializes stderr appends so chunk-N's stderr doesn't interleave with chunk-M's in the diagnostic log file. Per-iteration cancellation token (`lct` from `Parallel.ForEachAsync`) kills the running FFmpeg process via `ct.Register(proc.Kill)` if a sibling chunk throws.

The runtime memory guard (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 2 GB → safeChunkSize = 8`) is logged as a `[WARNING]` line in the render log when triggered. Do NOT increase `MaxDegreeOfParallelism` beyond 2 without testing QSV session contention — Intel iGPU has a hardware encoder session limit and exceeding it produces silent quality degradation, not an error.

### 6.12 `PhotosPerChunk = 12`, not 8 and not 50

`8` was the original OOM-safe value when blur ran at full 1920×1080. The cheap-blur fix (6.4) reduced per-photo filter graph memory ~16×, making larger chunks safe to attempt. `PhotosPerChunk = 50` was tested and **caused a regression** (Stage 2 = 149.5 s vs 100.4 s at PC=12) — QSV filter graph state cost grows non-linearly past ~20 real photos per chunk. Synthetic photos (320×240 solid color) failed to predict this because synthetic encode cost is startup-dominated, not per-frame-filter-traversal-dominated. The relationship inverts at real photo resolutions.

`12` is the empirically tuned value: fewer FFmpeg launches than 8, smaller filtergraphs than 50. For a 50-photo render this produces 5 chunks rendered in 3 parallel rounds. Do not change without re-running the 50-photo cold-cache timing test against the documented baseline (`159.43 s` total, `100.43 s` Stage 2).

### 6.13 `MaxLongerSide = 1920`, not 2160

The original `2160` cap was intended to preserve detail for the blur background. Cheap-blur (6.4) renders the blur source at 480×270 — extra detail above 1920 px is never used. Output is 1080p (1920×1080), so the foreground also gets no benefit. One-line constant change in `StagingNormalizer.cs`. ~16% less pixel data per photo through decode, manipulate, encode, and write. Contributed a measurable Stage 2 improvement (~6 s) likely from smaller staging file reads at the FFmpeg input stage. Do not raise back to 2160.

### 6.14 Stage 1 per-phase instrumentation (`StagingTimings` + `[TIMING-S1]` log block)

`NormalizedBitmapLoader.WriteJpeg` is instrumented with `Stopwatch.GetTimestamp()` deltas accumulated via `Interlocked.Add` into the public-static `StagingTimings` counters (`FileReadTicks`, `AutoOrientTicks`, `ColorSpaceTicks`, `ColorTypeTicks`, `ResizeTicks`, `StripWriteTicks`). `RenderJob.RunAsync` calls `StagingTimings.Reset()` before Stage 1 starts and writes a `[TIMING-S1]` block to the render log immediately after Stage 1 completes.

**Important: `FileRead` is a misnomer.** It wraps `new MagickImage(sourcePath)`, which performs file open AND full decode in one call. On a 50-photo cold-cache run of large DSLR-quality JPEGs the FileRead phase measured 3,319 ms avg/photo (68.5% of Stage 1) — dominated by libjpeg decode CPU work, not by file I/O. To definitively separate disk I/O from CPU decode, the call would need to be split into `File.ReadAllBytes` + `new MagickImage(bytes)` with separate instrumentation. See pre-launch open items in §10.

Use the `[TIMING-S1]` block when diagnosing unexpected Stage 1 slowness on new collections. Compare to the documented baseline (~46 s Stage 1 cold-cache for 50 large JPEGs).

### 6.15 Scan-pipeline per-phase instrumentation (`ScanTimings` + `[TIMING-SCAN]` log block)

The duplicate-detection scan has its own timing instrumentation, analogous to the render pipeline's `[TIMING-S1]` (6.14) but written to a **separate** log file. `DuplicateDetector.Detect()` returns a `ScanTimings` object (alongside the existing `DuplicateDetectionResult.Groups` / `DHashByPath`) carrying per-phase `Stopwatch` wall times for Phases 2–5: Exact match (SHA-256), dHash decode + compute, Pairwise comparison, and Group build (union-find). Phase 1 (Index = `FolderScanner.Scan`) runs *before* `Detect` is called, so `ScanningViewModel` measures it at the call site and stitches it onto the returned `ScanTimings`.

`ScanningViewModel.WriteScanTimingLog` writes the block to `%LOCALAPPDATA%\EasyPhotoShow\logs\scan_<timestamp>_<id>.log` (distinct `scan_` prefix so it never collides with the render logs). Format mirrors the render log column style (label padded to 27, seconds right-aligned in 9 cols, N2), and each line includes the count that matters for that phase (photos scanned, files SHA-hashed, pairs compared, groups found). Pair count = n × (n-1) / 2 where n = indexed photos.

**Design note:** the log write lives in `ScanningViewModel` (App layer), NOT in `DuplicateDetector` (Core) — Core stays free of file-I/O side effects, the same separation used for `LogAutoResolveResult`. If you add a phase, add a field to `ScanTimings` (`src/EasyPhotoShow.Core/DuplicateDetection/ScanTimings.cs`) and a line to `WriteScanTimingLog`.

**What it revealed (2026-05-28, 3,995-photo real scan):** dHash decode + compute is the scan bottleneck at **176.82 s (78% of the 226.59 s total)**; the O(n²) pairwise comparison — long assumed to be the thing to watch at scale — was trivially cheap at **1.23 s for 7,978,015 pairs**. See §10 and §11 for the resulting V1.1 optimization deferral.

---

## 7. Conventions

- **Records for value types.** `Photo` is a `record` with init-only properties; `with { Sha256 = ... }` is used to attach computed fields immutably during the pipeline.
- **Async/await with `CancellationToken` everywhere.** All long-running operations accept a token. `RenderJob` registers a kill on `Process` via `ct.Register`.
- **`IProgress<T>`.** Progress reporting is via `IProgress<T>` so the App can marshal back to the UI thread via `System.Progress<T>` (which captures `SynchronizationContext`).
- **Throw RenderException with a user-friendly message** when a render fails. The classification logic in `RenderJob.ClassifyFailure` maps stderr to a friendly bucket. Never surface raw FFmpeg stderr in UI.
- **Defensive over expressive.** `try/catch` around per-photo work in DuplicateDetector and PotentialDuplicatesMover. One bad photo shouldn't crash the whole operation.
- **Magick.NET-Q8 not Q16.** Q8 is plenty for photos; Q16 doubles memory for no benefit at this app's scope.

---

## 8. Tests

34 tests, all in `tests/EasyPhotoShow.Core.Tests/`. Run with `dotnet test`.

- `OrientationTests` (2) — load + write through `NormalizedBitmapLoader` correctly applies/strips EXIF orientation across values 1, 3, 6, 8 (the most common cases)
- `DHashTests` (4) — determinism, identical-input collision avoidance, Hamming distance basics
- `UnionFindTests` (2) — connected components, transitive bridging
- `RecommendedPhotoSelectorTests` (3) — resolution > size > time > path ranking, deterministic tiebreakers
- `GroupClassificationTests` (4) — ExactOnly vs HasVisualComponent classification; byte-identical sub-cluster extraction from visual groups
- `ExactGroupAutoResolverTests` (2) — auto-resolution of exact-copy groups (move non-recommended to PotentialDuplicates, remaining-photo accounting)
- `EventClustererTests` (3) — 30-min gap behavior, no-EXIF fallback
- `BestMixOrdererTests` (7) — similar-photo spacing (cluster members ≥ 4 slots apart), all-photos-preserved, determinism, empty/single-photo input, alphabetical fallback when no dHash/capture-time, 1,000-photo perf budget (< 200 ms)
- `RollingWindowTests` (5) — FIFO cap behavior of the `RollingWindow<T>` Core utility (capacity, eviction order, empty/clear, invalid-capacity guard)
- `RenderJobIntegrationTests` (2) — end-to-end render (multi-chunk + concat regression for the OOM fix). Skips if FFmpeg isn't present.

To add a new test, drop a `*.cs` file in the test project; xUnit auto-discovers `[Fact]` methods.

---

## 9. Dev environment notes

- **Working directory:** `E:\Dev\EasyPhotoShow` on the original developer machine. Adapt as needed.
- **Git** (initialized 2026-05-29). The repo is hosted privately at **`github.com/quickadvice/easyphotoshow`** — single `main` branch, accessed over HTTPS via the GitHub CLI's credential helper (`gh auth status` to confirm). Initial commit `94d3d83` was a snapshot of V1 with all 38 tests green. The `.gitignore` at the repo root excludes build output (`bin/`, `obj/`), Visual Studio user files, the FFmpeg binaries under `src/EasyPhotoShow.App/tools/ffmpeg/`, all MP3s (`*.mp3` — covers `Assets/Music/` presets and any working copies elsewhere), `Errors/`, `.claude/`, `installer/Output/`, `publish/`, and common OS junk (`Thumbs.db`, `Desktop.ini`, `.DS_Store`).
- **Memory store** (Claude Code only): `C:\Users\Admin\.claude\projects\E--Dev-EasyPhotoShow\memory\` contains compact notes on project context and load-bearing decisions. Most of that content has been promoted into this Documentation/ folder; the memory store is for ongoing AI-session continuity.
- **App icon assets** in `Icons/` at repo root are the source-of-truth design files. The `.ico` and 256-px PNG copied into `src/EasyPhotoShow.App/Assets/` are what the app uses at build time.
- **Build artifacts copy issue:** when EasyPhotoShow is running, `dotnet build` fails because the EXE/DLL are locked. The user will need to close the app between builds. (Not solvable cleanly without a process-attached debugger.)

---

## 10. Pre-launch open items

| Item | Notes |
|---|---|
| Music preset MP3s | **Done (2026-05-29).** Three royalty-free tracks wired in at `src/EasyPhotoShow.App/Assets/Music/`: `celebration.mp3` (97 s), `peaceful.mp3` (158 s), `reflective.mp3` (244 s). **Lowercase filenames are mandatory** — `MusicResolver.ResolveMp3Path` looks up `{preset.ToString().ToLowerInvariant()}.mp3`; renaming them breaks preset audio silently. All three durations are >60 s per the looping guidance. The csproj's `<None CopyToOutputDirectory>` rule copies them into the publish output and the installer bundles them. The MP3 source files themselves are gitignored (`*.mp3` global rule) and distributed via the installer, not source control. |
| Code signing certificate | $200-400/yr (Sectigo, DigiCert, Certum, etc.). Sign the final EXE + installer so Windows SmartScreen doesn't warn users on first install. Critical for the "calm, dependable" tone. |
| H.264 patent licensing | MPEG-LA has historically had a free-for-commercial-encoder tier up to 100K units/year. Confirm current terms with their licensing team before commercial distribution. |
| Trial-to-paid licensing | No mechanism implemented. Options: license key file, online activation, store-only distribution. Today `TrialLimits` is always-on. Design before launch. |
| Inno Setup installer | **Done (2026-05-29).** Script at `installer/EasyPhotoShow.iss`. Build steps: `dotnet publish src/EasyPhotoShow.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o publish\win-x64`, then `ISCC.exe installer\EasyPhotoShow.iss` → `installer/Output/EasyPhotoShowSetup.exe` (~143 MB compressed via `lzma2/ultra64` + solid). Bundles the full self-contained publish (FFmpeg + preset MP3s + .NET runtime). **The installed exe is `EasyPhotoShow.exe`, NOT `EasyPhotoShow.App.exe`** — the App csproj sets `<AssemblyName>EasyPhotoShow</AssemblyName>`, so the script's `MyAppExeName` reflects that. The app icon is embedded in the exe (`<Resource>`) and not emitted as a loose file by publish, so the script has a second `[Files]` line copying `easyphotoshow.ico` from source into `{app}\Assets\` so the `[Icons]` `IconFilename` references resolve. winget can install Inno Setup per-user — ISCC may land at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` rather than Program Files. Code signing still pending (separate row). |
| dHash threshold calibration | dHash threshold calibrated to 8 bits against a 49-photo real-world test collection (2026-05-26). Threshold 8 catches same-photo-with-text-overlay pairs at zero false-positive cost vs threshold 6. Pairs that are visually similar but differ in lighting, angle, or cropping may have Hamming distances of 20+ and cannot be caught by dHash threshold tuning alone. If catching same-scene photos taken at different times is important, consider adding pHash or a complementary perceptual similarity signal as a V1.1 workstream. |
| dHash decode optimization (V1.1) | **Deferred to V1.1 with full context, 2026-05-28.** `[TIMING-SCAN]` instrumentation on a real 3,995-photo scan showed dHash decode + compute is the scan bottleneck: **176.82 s = 78% of the 226.59 s total** (~44 ms/photo wall, already under `Parallel.For`). The cost is Magick.NET *decode*, not the hash compute or the pairwise compare (the O(n²) pairwise was only 1.23 s for ~8M pairs — see §11). The lever is decoding cheaper for the 9×8 dHash: decode at reduced resolution, or read an embedded EXIF thumbnail when present, instead of full-resolution decode. Not blocking for V1 (a 4-min scan on ~4,000 photos is acceptable for a one-time operation); revisit if scan time becomes a complaint. Do NOT change `DHash`/`DuplicateDetector` thresholds or parallelism as part of this — it is purely a decode-path change. |
| Blur sigma + scale calibration | Current `BlurSigma = 8` at 480×270 is empirical. Visual review against real slideshows to confirm. |
| HEIC concurrency limit (Option C) | libheif is single-threaded internally. Add `SemaphoreSlim(2)` around the Magick.NET HEIC decode call in `StagingNormalizer.Normalize` if a HEIC-heavy collection surfaces in beta testing. The current test collection is HEIC=0; option deferred. Implementation is ~10 minutes once justified by a real workload. |
| Conforming-photo skip (Option B) | For photos already conforming (JPEG + EXIF orientation = 1 + longer side ≤ 1920 px), `File.Copy` source → staging and bypass Magick.NET. Saves ~4,659 ms per eligible photo. Deferred because the current test collection's source photos are all > 1920 px, so the skip would fire for ~0% of photos. May be worth revisiting in V1.1 if a re-rendering workflow surfaces. |
| Stage 1 FileRead split instrumentation | Currently `new MagickImage()` wraps file open + full decode as one `FileRead` measurement. Splitting into `File.ReadAllBytes` + `new MagickImage(bytes)` with separate instrumentation would let us definitively isolate disk I/O from CPU decode. Useful diagnostic if Stage 1 unexpectedly slow on a different collection type. Low priority — defer until beta testing surfaces a need. |
| Thumbnail on-disk cache | Planned at `%LOCALAPPDATA%\EasyPhotoShow\thumbs\`; not implemented. Async thumbnail loading (`ThumbnailLoader.cs`, used by `DuplicateReviewView`) was implemented as a partial mitigation — thumbnails decode on a background thread so the UI doesn't block. The full disk cache would survive across sessions and save the decode entirely on repeat visits to the review screen. |
| dHash propagation bug | Resolved. dHash values computed during `DuplicateDetector` were previously discarded before reaching `BestMixOrderer`, silently making the variety tiebreaker a no-op. Fixed by returning `DuplicateDetectionResult` (Groups + DHashByPath dictionary) and re-attaching via `AttachDHashes` in `ScanningViewModel`. Regression test: `BestMixOrdererTests.Order_DHashTiebreaker_OverridesAlphabeticalWhenEventsTie`. |
| Opener / Closer slides feature | **Done (2026-05-28).** Optional opening and closing slides for the slideshow — each independently set to either a custom image or an app-generated text card (black or white background, auto-contrast text, ≤120 chars, live preview). Off by default. Lives in: `src/EasyPhotoShow.Core/Models/SlideContent.cs` (`SlideContent`/`CustomImageSlide`/`TextCardSlide`/`CardBackground`), `src/EasyPhotoShow.Core/Rendering/TitleCardGenerator.cs` (Magick.NET 1920×1080 q92 render with measure-and-shrink word wrap), `src/EasyPhotoShow.Core/Rendering/RenderJob.cs` (pre-Stage-1 bookend generation; pinning opener/closer to first/last positions on the staged-file list AFTER ordering, so `BestMixOrderer` never touches them), `src/EasyPhotoShow.App/Views/SlideshowCreationView.xaml` (new "Opening & Closing Slides" card with a shared `DataTemplate` for both sections), `src/EasyPhotoShow.App/ViewModels/SlideshowCreationViewModel.cs` (`SlideSectionViewModel` sub-VM keeping Custom-Image and Text states side-by-side, plus the silent dedup-if-image-in-collection logic), and `src/EasyPhotoShow.App/Session/SlideshowSession.cs` + `Models/SlideshowSettings.cs` (`OpenerSlide?` / `CloserSlide?` plumbing). Tests: `tests/EasyPhotoShow.Core.Tests/TitleCardGeneratorTests.cs` (4 tests — black/white background, 120-char max, empty text). Music fade-in (2 s) / fade-out (3 s) was added at the same time to `RenderJob.ConcatChunksAsync`. |
| Render cancel → resume | Today cancel deletes the partial output. Staging files survive briefly but are cleaned in `finally`. Could support resume from staging if rendering is the long pole. |
| Error-log retention | `%LOCALAPPDATA%\EasyPhotoShow\logs\*.log` accumulates forever. Consider rotating or capping count. |
| Git repository | **Done (2026-05-29).** Private repo at `github.com/quickadvice/easyphotoshow`, single `main` branch, HTTPS via `gh` CLI. Initial commit `94d3d83`. `.gitignore` is comprehensive (see §9 for the full exclusion set). |
| PhotosPerChunk tuning | With `MaxLongerSide = 960`, each staged file is ~4× smaller in memory. `PhotosPerChunk = 20` (vs current 12) should be safe and would reduce Stage 2 chunk overhead by ~40%. Test on the 894-photo collection before adopting. Do not change without measuring — see Incident 1 (OOM fix) in the load-bearing decisions. |

---

## 11. Incidents & decisions log

Captures the bumps hit during V1 build-out and what was learned. Future readers: don't re-make these.

### Incident 1 — 38-photo render OOM (2026-05-25)
**Symptom:** Real user render failed with `[fc#0] Error while filtering: Cannot allocate memory`. The log showed all 38 inputs being held in the filter graph simultaneously, each with split/scale/blur/overlay intermediates, then xfade-chained.

**Cause:** Single complex filtergraph (the original design) doesn't scale to many inputs. Especially gblur at 1920×1080 per photo. QSV piles on GPU memory pressure too.

**Fix:** Chunked rendering (≤8 photos per FFmpeg invocation, lossless concat) + cheap-blur trick (blur at 480×270, upscale) + force `ColorType=TrueColor` on staged JPGs to prevent mixed grayscale/yuvj444p inputs from tripping swscaler.

**Regression test:** `RenderJob_ProducesValidMp4_AcrossMultipleChunks` (20 mixed-aspect photos).

**Don't:** Switch to a single mega-filtergraph again. Don't reduce `PhotosPerChunk` without remeasuring; don't raise it without remeasuring.

### Incident 2 — LGPL FFmpeg has no libx264 (2026-05-25)
**Symptom:** Original encoder ladder ended at `libx264`. The BtbN LGPL Win64 build ships with `--disable-libx264` (libx264 is GPL).

**Cause:** Misalignment between "LGPL FFmpeg" (commercial-safe) and the libx264 fallback assumption.

**Fix:** Switched the software fallback to `libopenh264` (Cisco H.264, royalty-paid by Cisco, LGPL-compatible). Added `h264_mf` (Windows Media Foundation) as an intermediate Windows-universal fallback.

**Don't:** Reintroduce libx264. To use libx264 we'd need a GPL FFmpeg build, which would block commercial distribution.

### Incident 3 — xfade offset math (caught in review, 2026-05-25)
**Symptom:** Initial implementation had `offset = i * S - T`. Would have produced accelerating playback or stalls.

**Cause:** Transition's T-second overlap is *deducted from* segment time, not *added on top of*. Each new segment contributes `S - T` of new visible time.

**Fix:** `offset_i = i * (S - T)`. Inline comment in `FilterGraphBuilder.cs` for future editors.

### Incident 4 — Invisible progress bar (2026-05-25)
**Symptom:** User reported the rendering progress bar track was visible but the fill was invisible at any progress level.

**Cause:** Custom `ProgressBar.Template` had `PART_Indicator` but no `PART_Track`. WPF's `SetProgressBarIndicatorLength()` silently no-ops if `PART_Track` is null, so the indicator width was never set, so the fill rendered at 0 px width.

**Fix:** Bypassed `ProgressBar` entirely. Direct two-Border construction with a `FractionToWidthConverter` (IMultiValueConverter) computing `fraction × track.ActualWidth`. Simple, no PART machinery.

**Bonus polish:** Track is warm beige, fill is brand blue (high contrast), bar is 12 px tall with full corner radius, hold at 100% for 450 ms before navigating to Completion.

### Incident 5 — "Photo 38 of 38 / 20% complete" confusion (2026-05-25)
**Symptom:** During the brief transition between Stage 1 (Prepare) finishing and Stage 2 (Create) starting, the UI showed "Photo 38 of 38" alongside "20% complete". Looked broken.

**Cause:** Same "Photo X of Y" wording used across all per-photo phases. Photo count climbed during prep, hit total, then climbed again during create. Made it look like the app was going through the photos twice.

**Fix:** Phase-specific wording — "Preparing photo X of N" for Stage 1, "Building frame X of N" for Stage 2. Different verbs make different work obvious. Right side shows "Overall progress: X%" explicitly to anchor what the percent represents.

### Incident 6 — "Continue Without Review" label ambiguity (2026-05-25)
**Symptom:** Label suggested "apply sensible defaults and skip the per-card review," but the button actually included ALL photos with no duplicate handling. Users on the review screen had already opted into duplicate handling, so this was misleading.

**Fix:** Renamed to **"Include All Photos"** — honest about what happens. The "Use Recommended Choices" button (at the top) handles the "apply recommendations in one click" use case.

### Decision — Magick.NET-Q8 over Q16 (2026-05-25)
Q8 = 8 bits per channel. Plenty for photos at this app's scope. Q16 doubles memory for no perceptible benefit. Chose Q8.

### Decision — Magick.NET as the only decode path in Core (2026-05-25)
WPF imaging works for JPG/PNG but doesn't handle HEIC. To keep one decode path that handles all formats consistently — and to keep orientation handling in one place — Core uses Magick.NET exclusively. App's `ThumbnailConverter` has a WPF fast-path for non-HEIC thumbnails (orders of magnitude faster than Magick for small previews), with Magick fallback.

### Decision — Brand blue limited to progress bar only (2026-05-25)
The original UX/UI spec called for warm gold accents throughout. The app icon is brand blue. To resolve a visibility issue with the progress bar (warm gold on warm beige had low contrast), brand blue was introduced for the fill only. The rest of the UI stays warm gold. The split is intentional and signals: warm gold for *user actions*, brand blue for *system status*. Don't expand brand blue elsewhere without thinking about that signal.

### Incident 7 — PhotosPerChunk=50 regression (2026-05-26)
**Symptom:** Hypothesis was that consolidating to a single chunk for the 50-photo trial workload would reduce FFmpeg process startup overhead and beat the parallel-×2 baseline. Cold-cache measurement contradicted this. Single-chunk: Stage 2 = 149.50 s. Parallel×2 at PC=8: Stage 2 = 109.26 s.

**Cause:** QSV filter graph state cost grows non-linearly past ~20 inputs on real photos. A 50-input filtergraph with 49 chained xfade nodes is more expensive per-frame than 5–7 smaller graphs. Synthetic-photo tests failed to predict this because synthetic encode is startup-dominated; the relationship inverts at real photo resolutions.

**Fix:** Reverted to `PhotosPerChunk = 12` with parallel×2 (5 chunks, 3 parallel rounds for 50 photos). Stage 2 = 100.43 s in current cold-cache baseline. **Don't raise PhotosPerChunk past 12-15** without running the real 50-photo cold-cache timing test.

### Incident 8 — Stage 3 anomaly on first parallel run (2026-05-26)
**Symptom:** Stage 3 (concat + finalize) measured 20.28 s on the first run after compiling the parallel chunk implementation, then settled to ~12-13 s on every subsequent run. Reproduced sporadically on cold-cache reruns.

**Cause:** Not definitively identified. Suspected: warm-path initialization in FFmpeg's mp4 muxer / `+faststart` moov-atom rewriter, or first-touch disk cache state for the temp staging directory. Stable on subsequent runs.

**Fix:** None made. Stage 3 stays under the 15 s flag threshold on the vast majority of runs. If it starts exceeding 15 s consistently, instrument the Stage 3 FFmpeg invocation separately to isolate concat vs faststart cost.

### Decision — MaxLongerSide 2160 → 1920 (2026-05-26)
Cheap-blur renders blur source at 480×270, output is 1080p. No consumer of the staged JPEG uses detail above 1920 px. One-line constant change in `StagingNormalizer.cs`. ~16% less pixel data per photo through decode/resize/encode/write. Contributed ~6 s Stage 2 improvement via smaller FFmpeg input reads.

### Decision — Conforming-photo skip path (Option B) NOT implemented (2026-05-26)
Discussed during Stage 1 optimization analysis. Plan: for photos already conforming (JPEG + EXIF orientation = 1 + longer-side ≤ 1920 px), `File.Copy` source → staging and bypass Magick.NET, saving ~4,659 ms per eligible photo. Deferred because the test collection's source photos are essentially all > 1920 px (typical 12-24 MP camera output). The skip would fire for ~0% of photos in practice. Documented so a future contributor doesn't independently propose-and-defer the same optimization. **No `ExifOrientationNormal` property, no `IsConforming` method, no `[TIMING-S1-SKIP]` log line exists in code** — they are listed in this handoff only as the V1.1 design sketch, not as shipped features.

### Decision — Option C (HEIC concurrency cap) NOT implemented (2026-05-26)
Stage 1 instrumentation showed FileRead at 3,319 ms avg/photo despite parallel execution. Initial chunk-stderr chroma analysis (`yuvj444p` in ~60% of staging files) suggested HEIC-heavy sources, which would have made libheif's single-threaded global lock the bottleneck. A diagnostic scan-only log line in `ScanningViewModel` was added to validate against ground truth: **`[DIAG] HEIC=0, JPEG=48, PNG=2`**. The 4:4:4 chroma was from high-quality DSLR JPEGs, not HEIC. Option C deferred. DIAG instrumentation removed after confirming the result. The HEIC cap can be added in ~10 minutes of code if a HEIC-heavy collection surfaces in beta.

### Incident — dHash discard bug (resolved before instrumentation work, 2026-05-26)
**Symptom:** Best Mix variety tiebreaker silently a no-op on every run.

**Cause:** dHash values computed during `DuplicateDetector.Detect` were being discarded before reaching `BestMixOrderer`. The `Photo` records returned from `Detect()` no longer carried the dHash that was computed during grouping.

**Fix:** Changed `Detect()` to return `DuplicateDetectionResult` (Groups + DHashByPath dictionary). `ScanningViewModel` calls `AttachDHashes` to re-attach values to the working Photo list before navigating to either review or slideshow setup. Regression test: `BestMixOrdererTests.Order_DHashTiebreaker_OverridesAlphabeticalWhenEventsTie`.

### Decision — Stage 3 time scales with output file size (2026-05-26)
Stage 3 (concat + faststart moov rewrite) took 111 s on an 894-photo render (~700 MB output). This is expected: `+faststart` rewrites the moov atom to the file start, which is O(file size). For long slideshows, Stage 3 will always be noticeable. The indeterminate progress animation during Stage 3 (Task 7, 2026-05-26 UX batch) addresses the user-facing perception — the bar pulses while the work happens. Do not expect Stage 3 to be fast on large slideshows; if it ever appears stuck rather than slow, check that the pulse animation is active.

### Decision — BestMixOrderer algorithm replacement (2026-05-26)
Original event-clustering + proportional interleaving approach produced visible sections in the slideshow — similar photos from the same shoot appeared in runs. Product requirement is true variety with no detectable sections. Replaced with spacing-based algorithm: similarity clusters (dHash + capture time via union-find) are distributed across slots at enforced intervals. `EventClusterer.cs` retained but no longer called by `BestMixOrderer`. New tests in `BestMixOrdererTests.cs` assert the new contract (similar-photo spacing, alphabetical fallback, 1,000-photo perf budget); the previous `Order_SingleEvent_FallsBackToChronological`, `Order_MultipleEvents_InterleavesProportionally`, and `Order_DHashTiebreaker_OverridesAlphabeticalWhenEventsTie` tests were removed because they asserted the old (incorrect) behavior. See `Documentation/03_BestMix_Design.md` §3 for the new algorithm.

### Decision — swscaler "deprecated pixel format" warnings are cosmetic, accepted (2026-05-27)
Each chunk's FFmpeg stderr emits ~35 `deprecated pixel format used, make sure you did set range correctly` warnings from libswscale. **Investigated and closed as cosmetic.** They come from libswscale being initialised with the deprecated `yuvj420p` enum that the mjpeg decoder emits for our staged JPEGs. It is NOT a range problem — FFmpeg already tags the inputs as `yuvj420p(pc, ...)` (full/PC range is known) — so a `-color_range pc` input flag was tried (2026-05-27) and confirmed to have **zero effect** on the warning count. Output is correct. Truly eliminating them would require not feeding `yuvj*` frames to swscale at all (e.g. PNG staging), which we rejected for size/perf. Left as-is intentionally; documented inline in `RenderJob.BuildChunkArgs`. Don't re-investigate without new information.

### Decision — Scan-pipeline timing instrumentation `[TIMING-SCAN]` added (2026-05-28)
The render pipeline had detailed timing (`[timing]` stage blocks + `[TIMING-S1]`); the duplicate-detection scan had none. Added `ScanTimings` (returned by `DuplicateDetector.Detect`) + a `[TIMING-SCAN]` log block written from `ScanningViewModel` to `%LOCALAPPDATA%\EasyPhotoShow\logs\scan_*.log`. See §6.15 for the design (Core stays I/O-free; the App layer writes the log). No algorithm/threshold/parallelism changes — purely instrumentation.

### Finding — dHash decode is the scan bottleneck, not the O(n²) pairwise compare (2026-05-28)
The going-in hypothesis (carried in `02_DuplicateDetection_Design.md` §5) was that the naive O(n²) pairwise Hamming comparison would be the thing to watch at scale. The `[TIMING-SCAN]` block on a real **3,995-photo** scan overturned that:

```
[timing-scan] Index (walk + EXIF):           16.12 s   (3,995 photos, 318 unsupported)
[timing-scan] Exact match (SHA-256):         32.42 s   (1,652 files hashed)
[timing-scan] dHash decode + compute:       176.82 s   (3,995 photos)   ← 78% of total
[timing-scan] Pairwise comparison:            1.23 s   (7,978,015 pairs) ← trivially cheap
[timing-scan] Group build (union-find):       0.00 s   (960 groups found)
[timing-scan] TOTAL:                        226.59 s
```

Pairwise was **1.23 s for ~8M pairs** (XOR + popcount is nearly free); the real cost is Magick.NET *decoding* 3,995 images for the 9×8 dHash. **Verdict: defer dHash decode optimization to V1.1** (see §10 row). A ~4-minute scan on ~4,000 photos is acceptable for a one-time operation; the BK-tree idea in `02` §5 would optimize the wrong phase and is therefore NOT worth doing.

### Decision — Removed blank thumbnail cards from the scanning screen (2026-05-28)
The scanning screen previously streamed exact-duplicate "preview cards" (two thumbnails + an "Exact copy — set aside safely" label) into a rolling window as groups were found. On the live 3,995-photo scan the thumbnails never finished decoding mid-scan and displayed as **blank grey boxes for the entire duration** — which reads as "broken," not "loading." Removed the card grid and the "These exact copies have been safely set aside…" footer; **kept** the running "N exact duplicate files handled so far" count, which does the reassurance work without needing images to load. `ExactCopyCardViewModel.cs` deleted (orphaned); `RollingWindow<T>` retained as a tested Core utility. The duplicate **review** screen's thumbnails are unaffected — they're load-bearing for the user's decision and are intentionally kept. No scanning/detection logic changed.

### Validation — large-batch + multi-folder + sliding-window ETA verified live (2026-05-28)
A real **3,995-photo render across multiple source folders** completed successfully end-to-end (chunked render + concat held up at this scale; multi-folder dedup/scan validated). The render-screen ETA (now a sliding-window estimate — see `04_ExportPipeline_Design.md` §7) was watched live and behaved well (no climbing/jittery estimate). No regressions observed in the rest of the flow.

---

## 12. Where to look when X happens

| Symptom | Look at |
|---|---|
| App won't start | `App.xaml.cs` `OnStartup` exception, check `MainWindow.xaml` for parse errors |
| "EasyPhotoShow couldn't find the rendering engine" on Create | `FFmpegEnvironment.Resolve` — verify `tools/ffmpeg/ffmpeg.exe` exists next to the App binary |
| Progress bar invisible | Don't go back to templated `ProgressBar`. Verify `ProgressTrack.ActualWidth > 0` and `ProgressFraction > 0` |
| Photos appear sideways in output MP4 | `NormalizedBitmapLoader.WriteJpeg` must call `AutoOrient()` AND `Strip()`. Check `OrientationTests` |
| OOM during render | `FilterGraphBuilder` blur dimensions; `RenderJob.PhotosPerChunk` |
| "Cannot allocate memory" in render log | Confirm chunked rendering is still chunking. Check `PhotosPerChunk` constant and that `RenderChunksAsync` is being called, not a single mega-graph |
| Duplicate detection misses obvious duplicates | `DHash.SimilarityThresholdBits` (raise to be more permissive); also verify EXIF orientation isn't causing false negatives |
| Default save folder is wrong | `SlideshowCreationViewModel.DefaultSaveFolder` — should walk SourceFolders, fall back to MyVideos only if none usable |
| Default filename doesn't auto-increment | `SlideshowCreationViewModel.AvailableFilename` — caps at 999 iterations |
| Render stuck or never reports progress | `RenderJob.RunFFmpegAsync` stdout reading loop; check `ProgressParser.Feed` parses the format FFmpeg is emitting |
| MP4 won't play in QuickTime/WhatsApp | Encoder args missing `-pix_fmt yuv420p` |
| Trial limits not enforced | `TrialLimits.ExceedsPhotoCap` / `ExceedsDurationCap` checks in `SlideshowCreationViewModel.RecomputeRuntime` |
| Music preset silently has no audio | `MusicResolver.ResolveMp3Path` — check the file exists at `Assets/Music/{name}.mp3` |
| HEIC photos not loading | `Magick.NET-Q8-AnyCPU` package present? It bundles libheif. Magick.NET on .NET 8 should "just work" |
| Tests fail with "no FFmpeg" | `RenderJobIntegrationTests.FFmpegBundled` walks for `tools/ffmpeg/`; ensure binaries are present or PATH includes FFmpeg |
| Render slower than expected after a code change | Check `PhotosPerChunk` (must be 12) and that `Parallel.ForEachAsync` is intact in `RenderJob.RenderChunksAsync`. Confirm the cheap-blur path in `FilterGraphBuilder` still uses 480×270. Run the 50-photo cold-cache timing test against the documented baseline (~159 s total, ~100 s Stage 2). |
| Stage 1 unexpectedly fast | Almost always warm OS file cache from a recent identical render. Reboot, OR `rammap -Accepteula -Et` before re-measuring. Cold-cache Stage 1 baseline is ~46 s for 50 large JPEGs. |
| Stage 1 unexpectedly slow on a HEIC-heavy collection | Option C (HEIC concurrency cap) is not yet implemented. Add `SemaphoreSlim(2)` around the Magick.NET HEIC decode call in `StagingNormalizer.Normalize` for `PhotoFormat.Heic` photos only. JPEG/PNG must continue to run at full `ProcessorCount - 1` parallelism. See §10 pre-launch open items. |
| Need to know whether Stage 1 cost is I/O or CPU | The current `[TIMING-S1] FileRead` line wraps both. Split `new MagickImage(path)` into `File.ReadAllBytes` + `new MagickImage(bytes)` and instrument each separately. See §10. |

---

## 13. Recommended first reading order for a new contributor

1. `Documentation/README.md` (entry index — points back here)
2. `01_Product_Specification.md` (what V1 is)
3. This document (you are here)
4. Skim `02`, `03`, `04`, `05` — read the section that matches the area you'll touch
5. `tests/EasyPhotoShow.Core.Tests/` — read a couple of tests, they're a fast way to internalize what the code expects
6. Run `dotnet build` and `dotnet test`. Run the app with `dotnet run --project src/EasyPhotoShow.App`. Drop in a folder of photos. Hit Create Slideshow. Watch it work.

After that, pick a TODO from §10 and you're shipping.
