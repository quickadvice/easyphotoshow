# EasyPhotoShow V1 — MP4 Export Pipeline Design

## Status
**Implemented with significant evolution from original design.** Last reviewed: 2026-05-28. Validated end-to-end on a real **3,995-photo render across multiple source folders** (2026-05-28) — chunked render + concat and the sliding-window ETA (§7) held up at that scale.

Pipeline lives in `src/EasyPhotoShow.Core/Rendering/`. Key files: `RenderJob.cs` (orchestrator), `StagingNormalizer.cs` (Stage 1), `FilterGraphBuilder.cs` (filter graph), `EncoderProbe.cs` (hardware detect), `FFmpegEnvironment.cs` (binary discovery), `ProgressParser.cs` (FFmpeg progress events), `MusicResolver.cs`, `MusicMetadataProbe.cs`.

Integration test: `RenderJobIntegrationTests.RenderJob_ProducesValidMp4_AcrossMultipleChunks` renders 60 synthetic mixed-aspect photos end-to-end and validates the MP4 (covers the multi-chunk + concat path at the current `PhotosPerChunk=12` setting).

### What changed vs original design
The original design proposed a single complex FFmpeg filtergraph for the entire slideshow. That approach OOM'd on a 38-photo render in production. The current implementation has these additional changes layered on top:

- **Chunked rendering** at `PhotosPerChunk = 12` (was originally 8; briefly tested at 50 and reverted, see Incident 7) with a runtime fallback to 8 if available memory < 2 GB.
- **Parallel chunk execution** via `Parallel.ForEachAsync(MaxDegreeOfParallelism = 2)` — chunks render 2 at a time. Progress is aggregated via `chunkFractions[]` summation under `aggregateLock`. `LogLock` serializes stderr appends. Per-chunk cancellation via `ct.Register(proc.Kill)`. See §6.11 of `06_Code_Handoff.md` for rationale; do not raise to 3+ without testing QSV session contention.
- **Cheap-blur trick**: aspect-fill background is rendered at 480×270 then upscaled to 1920×1080 — visually identical to blurring at full size but ~16× cheaper in memory + CPU.
- **`MaxLongerSide = 960`** (was 1920, originally 2160). Staging at 960 px is sufficient because FFmpeg upscales the foreground to fit 1920×1080 regardless and the blurred background is destroyed-detail at any input size. ~50% Resize-time reduction vs 1920.
- **Encoder ladder**: LGPL FFmpeg builds don't include libx264, so `libopenh264` is the software fallback. Hardware ladder: NVENC → QSV → AMF → MediaFoundation → libopenh264.
- **Stage 1 per-phase instrumentation** (FileRead / AutoOrient / ColorSpace / ColorType / Resize / StripWrite) accumulated via `Interlocked.Add` and logged as a `[TIMING-S1]` block after Stage 1 completes. Diagnostic only.

See §13 for the full incident log including the PhotosPerChunk=50 regression and Stage 3 anomaly.

---

## 1. Goals (from spec §13, §14)

- Reliably fast rendering on the user's hardware
- Stable on large projects (no OOM, no crashes mid-render)
- Output: MP4, 1080p, H.264 + AAC, web-playable (`+faststart`)
- Calm, accurate progress reporting and ETA
- Clean cancellation (no orphaned partial files)
- No FFmpeg jargon exposed to users (spec §18)
- Honor EXIF orientation **everywhere** (see §3 — non-negotiable)
- Handle non-16:9 photos via blurred-fill background

---

## 2. Pipeline Overview

Three stages. Stages 2 and 3 invoke FFmpeg one or more times.

```
Stage 1: Normalize          (decode + EXIF-rotate + pre-scale + write staging JPEGs)
Stage 2: Render (chunked)   (1 FFmpeg invocation per ~8-photo chunk)
Stage 3: Concat + finalize  (1 FFmpeg invocation: -c:v copy + audio + validate + move)
```

Staging is critical. It makes Stage 2 deterministic: FFmpeg always receives a normalized stream of 1920×1080-fit, EXIF-corrected, sRGB, TrueColor JPEGs. No format quirks, no orientation surprises, no HEIC decoding inside FFmpeg, no grayscale/yuvj444p mixed inputs to confuse swscaler.

**Code entry point:** `RenderJob.RunAsync()` in `src/EasyPhotoShow.Core/Rendering/RenderJob.cs`.

---

## 3. EXIF Orientation Handling — First-Class Concern

**FFmpeg does NOT auto-apply EXIF orientation to still-image inputs.** A photo shot in portrait on an iPhone — with the sensor pixels stored landscape and orientation tag = 6 — will appear **sideways in the output video** if naively fed to FFmpeg's image2 demuxer. This is the #1 cause of "why is my video rotated" bugs in every consumer slideshow tool that has ever shipped wrong.

We handle this by **fully resolving orientation during Stage 1 (Normalize)**, before FFmpeg ever sees the image. The pixels written to the staging file are already in their visually-correct orientation, and the staging file is written with the EXIF profile stripped (so any downstream tool sees orientation = 1 / unset).

### Orientation transform

Handled by `Magick.NET`'s `AutoOrient()`, which exhaustively processes all 8 EXIF orientation values (Normal, FlipHoriz, Rotate180, FlipVert, Transposed, Rotate90CW, Transverse, Rotate90CCW). After transform, the bitmap's `Width`/`Height` reflect the visual dimensions, not the sensor dimensions.

### Why not use FFmpeg's `transpose` filter?

We could apply `transpose` inside the filtergraph based on per-image EXIF reads, but:

- The filtergraph would balloon in complexity (one branch per orientation per photo)
- Debugging an off-by-one orientation bug across N photos is miserable
- HEIC decode already happens in our process (Stage 1) for libheif reasons — orientation belongs in the same step
- Normalized staging files are also reusable for re-renders with different transitions/music

Cleaner to do it once, in our code, in one place.

### Cross-cutting consistency

The same EXIF-corrected pixels are used:

- For thumbnails in the duplicate review screen (`02_DuplicateDetection_Design.md` §9)
- For dHash computation in duplicate detection
- For dHash distance in Best Mix
- For the staging files fed to FFmpeg here

**One decode path, one orientation transform, one source of truth.** Centralized in `NormalizedBitmapLoader` (`src/EasyPhotoShow.Core/Imaging/NormalizedBitmapLoader.cs`).

### Test

`OrientationTests` in `tests/EasyPhotoShow.Core.Tests/` covers EXIF orientations 1, 3, 6, and 8 and verifies:
- `Load()` returns the correct visual dimensions after rotation
- `WriteJpeg()` produces an output with orientation = 1 / unset

---

## 4. Stage 1 — Normalize

For each photo in render order:

1. Open with `Magick.NET` (handles JPG, PNG, HEIC, HEIF via one decode path)
2. `AutoOrient()` — applies EXIF orientation transform
3. `ColorSpace = sRGB` — avoid color shifts in the MP4
4. `ColorType = TrueColor` — forces consistent pixel format (prevents grayscale/yuvj444p mixed inputs from tripping swscaler downstream)
5. Pre-scale so the longer dimension is ≤ **960 px** (`MaxLongerSide`). Staging at 960 px produces visually identical 1080p output because FFmpeg scales the foreground to fit regardless, and the blurred background destroys all detail at any resolution. 960 reduces Magick.NET Resize time by ~50% vs 1920.
6. Write to `<staging>/<index:00000>.jpg` at quality 92 (`JpegQuality`), with the EXIF profile `Strip()`-ped

**Code:** `StagingNormalizer.Normalize()` in `src/EasyPhotoShow.Core/Rendering/StagingNormalizer.cs`. Uses `Parallel.For` with `MaxDegreeOfParallelism = Math.Min(4, Math.Max(2, ProcessorCount - 1))` — raised from 2 (Issue 9, 2026-05-26). Cap at 4 bounds libheif memory pressure on HEIC-heavy collections.

Staging folder: `%TEMP%\EasyPhotoShow\staging\<session_guid>\`. Cleaned up on success and on error; preserved on cancel (so a retry doesn't redo Stage 1 — TODO not yet wired but the directory survives).

### Per-phase instrumentation (`[TIMING-S1]` log block)

`NormalizedBitmapLoader.WriteJpeg` is instrumented with per-phase `Stopwatch.GetTimestamp()` deltas accumulated via `Interlocked.Add` into the static `StagingTimings` counters. `RenderJob.RunAsync` calls `StagingTimings.Reset()` before Stage 1 and writes a `[TIMING-S1]` block to the existing render log immediately after Stage 1 completes:

```
[TIMING-S1] FileRead:   <ms> total  (<ms> avg/photo)
[TIMING-S1] AutoOrient: <ms> total  (<ms> avg/photo)
[TIMING-S1] ColorSpace: <ms> total  (<ms> avg/photo)
[TIMING-S1] ColorType:  <ms> total  (<ms> avg/photo)
[TIMING-S1] Resize:     <ms> total  (<ms> avg/photo)
[TIMING-S1] StripWrite: <ms> total  (<ms> avg/photo)
[TIMING-S1] TOTAL:      <ms>         (<ms> avg/photo)
```

`FileRead` is a misnomer — it wraps `new MagickImage(sourcePath)`, which performs **file open AND full decode** in one call. On a 50-photo cold-cache run of large DSLR-quality JPEGs (HEIC=0, JPEG=48, PNG=2 confirmed), FileRead measured 3,319 ms avg/photo (68.5% of Stage 1). The dominant cost is libjpeg decoding 4:4:4-chroma high-resolution photos through Magick.NET, not file I/O. To definitively separate disk I/O from CPU decode, the call would need to be split into `File.ReadAllBytes` + `new MagickImage(bytes)` and instrumented separately — see open items in `06_Code_Handoff.md` §10.

### Confirmed cold-cache performance (50-photo, JPEG-only collection)
- Stage 1 wall: 46.14 s
- TOTAL accumulated across workers: 242.4 s (effective parallelism ≈ 5.3 workers)
- Per-photo Stage 1 wall ≈ 920 ms

This is the practical ceiling for the current Magick.NET approach on large-source JPEGs. Async I/O prefetch or a faster decoder (libjpeg-turbo direct bindings) would be the next levers but are outside V1 scope.

### Conforming-photo skip (proposed, NOT implemented)
A planned optimization for V1.1: photos that already conform to the staging requirements (JPEG format + EXIF orientation = 1 + longer side ≤ 1920 px) could be `File.Copy`'d directly to staging, bypassing the full Magick.NET pipeline and saving ~4,659 ms per eligible photo. The path was discussed during Stage 1 optimization analysis (see Incident "Option B deferred" in §13). It was deferred because the test collection's source photos are all > 1920 px, so the skip would fire for ~0% of photos and provide no measurable benefit. May be revisited if a workflow surfaces with already-processed photos (e.g., re-rendering from a prior export folder).

**Disk budget:** ~300 KB – 1.0 MB per staged photo at the new 1920 cap. 1,000 photos → ~600 MB peak. Future enhancement could downscale further if free space is tight.

---

## 5. Stage 2 — Chunked Render

Each chunk = up to `PhotosPerChunk = 12` photos rendered as a self-contained MP4. Trade-off: a hard cut (no transition) appears at chunk boundaries every 12 photos. Acceptable for V1; the alternative (single mega-filtergraph) OOM'd on large inputs.

For a 50-photo render at `PhotosPerChunk = 12` this produces 5 chunks. With `MaxDegreeOfParallelism = 2` chunks render in 3 parallel rounds (chunks 0+1, then 2+3, then 4 alone).

**Memory guard:** at the top of `RenderJob.RunAsync`, `GC.GetGCMemoryInfo()` is inspected. If `TotalAvailableMemoryBytes < 2 GB`, `safeChunkSize` falls back to `LowMemoryChunkSize = 8` and a `[WARNING] Low memory (<2GB available). Using PhotosPerChunk=8 for stability.` line is written to the render log. `PhotosPerChunk` was tested at 50 (single chunk for a 50-photo render) and **reverted** — see Incident 7. QSV filter graph state cost grows non-linearly past ~20 inputs on real photos: the single-chunk 50-photo render measured 149.5 s Stage 2 versus 100.4 s for the 5-chunk parallel render at `PhotosPerChunk = 12`. Do not raise past 12-15 without re-running the cold-cache timing test.

**Parallel execution:** `Parallel.ForEachAsync(MaxDegreeOfParallelism = 2)` runs chunks two at a time. Progress is aggregated via `chunkFractions[]` summed under `aggregateLock` so the UI progress bar moves monotonically (not last-reporter-wins). `LogLock` serializes stderr appends. Per-iteration cancellation token kills the running FFmpeg process via `ct.Register(proc.Kill)` if a sibling chunk throws.

**Code:** `RenderJob.RenderChunksAsync()` and `BuildChunkArgs()`.

For each chunk:

```
ffmpeg -hide_banner -y -nostats -progress pipe:1 \
  -loop 1 -t <seconds_per_photo> -i <staging>/00000.jpg \
  -loop 1 -t <seconds_per_photo> -i <staging>/00001.jpg \
  ... (up to 12) \
  -filter_complex "<per-chunk graph>" -map "[vout]" -an \
  -c:v <encoder> <encoder-args> -r 30 \
  <staging>/chunk_000.mp4
```

### 5.1 Per-photo filter chain (blurred-fill aspect, cheap blur)

For each input `[Ni]`:

```
[Ni:v]split=2[srcNa][srcNb];
[srcNa]scale=480:270:force_original_aspect_ratio=increase,crop=480:270,gblur=sigma=8,scale=1920:1080,setsar=1[bgN];
[srcNb]scale=1920:1080:force_original_aspect_ratio=decrease,setsar=1[fgN];
[bgN][fgN]overlay=(W-w)/2:(H-h)/2,fps=30,format=yuv420p,setpts=PTS-STARTPTS[segN];
```

- `[bgN]` = source scaled UP to **480×270** + cropped + blurred (σ=8) + scaled to 1920×1080. **Why cheap-blur:** gblur at full 1920×1080 is memory-expensive and was the proximate cause of OOM during chunked-vs-single-filtergraph debugging. The blurred result is destroyed-detail anyway, so blurring at 480×270 then upscaling produces a visually identical result at ~16× lower memory and CPU.
- `[fgN]` = source scaled DOWN to fit inside 1920×1080 preserving aspect
- Overlay centers the sharp foreground over the blurred background
- For 16:9 photos, the foreground exactly fills the frame and the blurred background is invisible

**Code:** `FilterGraphBuilder.Build()`.

Constants:
- `OutputWidth = 1920`, `OutputHeight = 1080`
- `FrameRate = 30` fps
- `TransitionSeconds = 1.0`
- `BlurSourceWidth = 480`, `BlurSourceHeight = 270`
- `BlurSigma = 8`

### 5.2 Transition chain (xfade)

Chain `[seg0]`, `[seg1]`, ... with `xfade` transitions inside each chunk:

```
[seg0][seg1] xfade=transition=<mode>:duration=1.0:offset=<i*(S-T)> [x1];
[x1][seg2]   xfade=transition=<mode>:duration=1.0:offset=<i*(S-T)> [x2];
...
```

**Offset math:** for chained xfade, `offset_i = i * (S - T)` where S = seconds per photo and T = 1.0 (transition duration). The transition's T seconds *overlap* (rather than add to) the segment time. Easy to get wrong if anyone edits `FilterGraphBuilder.cs` — getting it wrong produces accelerating playback or stalls.

**Transition mapping (spec §11):**

| User-facing name | FFmpeg `xfade` mode | Notes |
|---|---|---|
| Fade | `fade` | crossfade through black-mid |
| Smooth | `smoothleft` | originally requested as "Morph"; FFmpeg has no morph filter, smoothleft is the LGPL-compatible substitute |
| Push | `slideleft` | full slide |
| Dissolve | `dissolve` | pixelated random blend |
| Zoom | `zoomin` | scale-in to next photo |
| Random | one of `{fade, smoothleft, slideleft, dissolve, zoomin}` per transition, seeded by a stable hash of the chunk's file paths | curated subset — skips potentially cheesy modes like `pixelize` |

### 5.3 Chunk audio

Chunks render **silent** (`-an`). Audio is added in Stage 3 during concat, so it doesn't have to be re-applied per chunk.

### 5.4 Encoder selection ladder

Detected once and cached. The LGPL FFmpeg build ships with **`--disable-libx264`**, so the ladder uses `libopenh264` (Cisco-licensed, LGPL-compatible) as the universal software fallback, NOT libx264.

| Priority | Encoder | Detected via |
|---|---|---|
| 1 | `h264_nvenc` | NVIDIA GPU + drivers; FFmpeg `-encoders` lists nvenc + 1-frame test passes |
| 2 | `h264_qsv` | Intel iGPU with QSV; same listing + test |
| 3 | `h264_amf` | AMD GPU with AMF runtime; same listing + test |
| 4 | `h264_mf` | Windows Media Foundation H.264; available on Windows 8+ universally |
| 5 | `libopenh264` | LGPL-compatible software encoder; always available in BtbN LGPL build |

**Code:** `EncoderProbe.Pick()` in `src/EasyPhotoShow.Core/Rendering/EncoderProbe.cs`. Test encode (1-frame `color=black` source) at first selection confirms the hardware encoder actually works — driver mismatches are common.

**⚠️ Do not switch back to libx264** without first switching to a GPL FFmpeg build. That would contaminate the licensing story for a commercial paid product.

### 5.5 Encoder quality settings

Targeting visually-indistinguishable-from-source at reasonable file size:

| Encoder | Args |
|---|---|
| h264_nvenc | `-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 23 -pix_fmt yuv420p` |
| h264_qsv | `-c:v h264_qsv -preset medium -global_quality 23 -pix_fmt yuv420p` |
| h264_amf | `-c:v h264_amf -quality balanced -rc cqp -qp_i 22 -qp_p 22 -pix_fmt yuv420p` |
| h264_mf | `-c:v h264_mf -rate_control quality -quality 65 -pix_fmt yuv420p` |
| libopenh264 | `-c:v libopenh264 -b:v 6M -pix_fmt yuv420p` |

`yuv420p` is mandatory for QuickTime / WhatsApp / generic-player compatibility. `+faststart` is applied at the concat stage (moves the moov atom to the file start so the MP4 is streamable).

**Expected file size:** ~30-50 MB per minute of 1080p output. 10-min slideshow ≈ 300-500 MB.

---

## 6. Stage 3 — Concat + Finalize

After all chunks render, concatenate losslessly with `-c:v copy` and add audio in one final FFmpeg invocation:

```
ffmpeg -hide_banner -y -nostats \
  -f concat -safe 0 -i <staging>/concat.txt \
  [-stream_loop -1 -i <music.mp3>] \
  -map 0:v -c:v copy \
  [-map 1:a -c:a aac -b:a 192k -ar 48000 -shortest] \
  -movflags +faststart \
  <output>.part.mp4
```

`concat.txt` lists chunk paths in order. With `-c:v copy`, no re-encode happens — chunks are stitched as-is, fast.

**Code:** `RenderJob.ConcatChunksAsync()`.

Then:
1. Validate output: file exists, size > 1 KB (sanity)
2. If user-chosen output path exists, delete it
3. Move `.part.mp4` to final output path
4. Delete staging folder (always — on success in the `try`, on failure in `finally`)
5. Report `Complete` with fraction 1.0; UI holds 100% for 450 ms before navigation

If validation fails (corrupt output, zero-size): surface calm error ("Something went wrong while finishing your slideshow. Please try again."), preserve full FFmpeg stderr to `%LOCALAPPDATA%\EasyPhotoShow\logs\<timestamp>_<sessionPrefix>.log` for support.

---

## 7. Progress Reporting

Invoke FFmpeg with `-progress pipe:1 -nostats`. Parse key=value lines from stdout in `ProgressParser`:

- `frame=` → current frame count
- `out_time_us=` or `out_time_ms=` → microseconds rendered so far
- `fps=` → encoding speed
- `progress=end` → done

### Stage labeling (UX-friendly)

The `RenderingViewModel` maps `RenderStage` enum values to user-facing strings. Phase-specific photo wording (Preparing photo X of N vs Building frame X of N vs All frames built) prevents the "going through the photos twice" confusion that an earlier UI iteration produced.

| RenderStage | StageLabel (Lead text) | PhaseProgressText (left small) |
|---|---|---|
| PreparingPhotos | "Preparing your photos..." | "Preparing photo X of N" → "All photos prepared" |
| CreatingSlideshow | "Creating video and adding transitions..." | "Building frame X of N" → "All frames built" |
| AddingMusic | "Adding music..." | "Photos and frames complete" |
| FinalizingSlideshow | "Saving your slideshow..." | "Photos and frames complete" |
| Complete | "Almost done..." | "Slideshow ready" |

Right-aligned: `"Overall progress: X%"` (semibold). Bottom centered: ETA text.

### Stage progress weighting

- PreparingPhotos: 0% → 20%
- CreatingSlideshow (chunked render): 20% → 94%
- AddingMusic / Concat: 94% → 97%
- FinalizingSlideshow: 97% → 100%

### ETA — sliding-window rate (Stage 2)

The Stage 2 ETA is computed in `RenderJob.RenderChunksAsync` (`ReportOverall`) from a **sliding window of recent photo-completion samples**, not a cumulative average from t=0. Rationale: a cumulative average produced a *climbing* ETA in practice — QSV session warm-up and parallel-pipeline init make the first 1–2 chunks faster than steady state, so the early "elapsed ÷ done" rate was optimistically high and the displayed estimate visibly climbed as the true pace asserted itself (observed 6→11→16→17 min on a ~2,729-photo render, which looks broken even though the math is just correcting).

How it works now:
- Each progress report appends a `(timestamp, totalPhotosDone)` sample; samples older than `EtaWindowPhotos = 75` photos (measured back from the latest) are trimmed. Rate = photos-per-second over that trailing window.
- The first estimate is **suppressed** until BOTH ≥ 5% of total photos AND ≥ 2 chunks have completed (`EtaMinFractionForFirstEstimate` / `EtaMinChunksForFirstEstimate`), so the first number shown is computed on a stable rate, not a single-chunk anomaly.
- `remainingPhotos ÷ photosPerSec`, rounded UP to the nearest 30 s.

`RenderingViewModel` formats the resulting `TimeSpan` for display:
- `< 30 s` → "Almost done"
- `< 60 s` or rounded == 1 → "About 1 minute remaining"  (singular fixed)
- Otherwise → "About N minutes remaining"
- Before the threshold is met → "Estimating time..."

**Verified live (2026-05-28):** on the 3,995-photo render the displayed estimate descended/held steadily rather than climbing — the regression the sliding window was built to fix did not recur.

---

## 8. Cancellation

User clicks Cancel (spec §14 "Rendering Controls"):

1. The `RenderingViewModel.Cancel()` command calls `_cts.Cancel()`
2. `RenderJob` cooperatively checks `ct.ThrowIfCancellationRequested()` between chunks and after FFmpeg waits
3. The currently-running FFmpeg process is killed via `proc.Kill(entireProcessTree: true)` (registered via `ct.Register`)
4. `OperationCanceledException` propagates; `finally` deletes the staging directory
5. The `.part.mp4` (if any) is deleted in the cancel catch block
6. ViewModel navigates back to the slideshow setup screen with settings intact

The "Closing During Render" warning (`MainWindow.OnClosing`) calls the same cancel command if the user picks "Stop and Exit".

---

## 9. Error Handling

`RenderException` (defined in `RenderException.cs`) carries a `RenderFailureKind` and a user-friendly message. The catch in `RenderJob` classifies FFmpeg stderr into the kind:

| Stderr pattern | RenderFailureKind | User-facing message |
|---|---|---|
| "No space left", "disk full" | DiskFull | "There's not enough space on the drive to finish your slideshow. Please free up space and try again." |
| "Permission denied", "Access is denied" | OutputUnwritable | "EasyPhotoShow couldn't save to that location. Please choose a different folder." |
| "No such file or directory" | SourceUnavailable | "One of your photos became unavailable during rendering. Please check that all drives are connected and try again." |
| "Cannot allocate memory", "Error initializing", "Could not find encoder" | EncoderFailure | "Slideshow rendering hit a snag. Please try again." |
| Anything else | Unknown | "Something unexpected happened. Please try again." |

All cases also surface the log file path in the user-facing message so support can read it.

**Never** show raw FFmpeg stderr in the UI.

---

## 10. Long Paths and External Drives

- Long-path support is enabled in `src/EasyPhotoShow.App/app.manifest` via `<longPathAware>true</longPathAware>`
- Drive-disconnect handling is reactive (the FFmpeg error message is classified as `SourceUnavailable` if a source file vanishes mid-render)
- Proactive `FileSystemWatcher` drive-removed events are not implemented; defer unless users hit issues

---

## 11. Performance Budget

All numbers below are **measured cold-cache** values on the developer machine (Windows 11, Intel CPU + QSV iGPU, SATA SSD), on a 50-photo collection of large DSLR-quality JPEGs (HEIC=0, JPEG=48, PNG=2, longer-side range 538–1920 px in source, no orientation rotation needed for most). Cold cache means the OS file cache was emptied via `rammap -Accepteula -Et` immediately before the render.

| Config                            | Stage 1 | Stage 2 | Stage 3 | Total   | Notes                                  |
|-----------------------------------|--------:|--------:|--------:|--------:|----------------------------------------|
| Original baseline (PC=8, sequential) | 43.39 s | 146.54 s | 12.88 s | **202.82 s** | starting point                  |
| PC=8, parallel×2 (best run)          | 49.32 s | 109.26 s | 12.55 s | **175.88 s** | first parallel result            |
| PC=50, sequential                    | 47.19 s | 149.50 s | 11.14 s | **207.83 s** | regression — reverted (Incident 7) |
| **PC=12, parallel×2 (current state)** | **46.14 s** | **100.43 s** | **12.86 s** | **159.43 s** | confirmed cold-cache baseline |

**Stage 2 has reached the practical QSV hardware encoder session ceiling** at ~100 s for 50 photos at `secondsPerPhoto = 4.0` (~200 s of output video). Further Stage 2 improvement requires a different encoder or hardware.

**Stage 1 estimate (post Issue 8+9, 2026-05-26):** ~5 s for 100 photos on a 4-core machine, derived from the 50-photo baseline of ~46 s + the ~50% Resize reduction (MaxLongerSide 1920→960) + ~2× parallelism (workers 2→min(4, PC-1)). For large-file collections (many photos > 5 MB), Stage 1 can still dominate — measured at 689 ms/photo on a real-world 894-photo graduation collection **before** the MaxLongerSide reduction; post-reduction expectations are below.

**Original observation kept for context:** `FileRead` (which wraps `new MagickImage()` — file open + full decode in one call) was 68.5% of Stage 1 cost at ~3,300 ms per large source JPEG on the 50-photo benchmark. Most of that is libjpeg decoding 4:4:4-chroma high-resolution photos through Magick.NET, not file I/O. Further reductions would require splitting the FileRead measurement (file open vs decode), switching to a faster JPEG decoder (libjpeg-turbo direct bindings), or implementing the conforming-photo skip path for already-processed photos.

**Warm cache caveat:** measurements with warm OS file cache (e.g., running the same render twice in a row without clearing the cache) come in dramatically lower (~22 s Stage 1) but do not represent first-render user experience. Always reboot or run `rammap -Et` between measurements.

Larger projects scale roughly linearly per stage. A 1,000-photo, 40-min slideshow with QSV: expect ~15-20 minutes total. ETA reporting handles this transparently.

**Large-batch validation (2026-05-28):** a real **3,995-photo render across multiple source folders** completed successfully end-to-end. The chunked-render + lossless-concat pipeline held at this scale (no OOM, no mid-render failure), multi-folder scanning/dedup worked, and the sliding-window ETA (§7) behaved (no climbing estimate). This is the largest batch validated to date and supersedes the earlier 894-photo graduation collection as the high-water mark. (The matching scan-side timing breakdown lives in `02_DuplicateDetection_Design.md` §10.)

---

## 12. Open Items

1. **Transition duration tunable?** V1 hardcodes 1.0 s. Some users may want snappier (0.5 s) or more cinematic (2.0 s). Defer to V1.1 — adding it now bloats the setup screen.
2. **Chunk boundary hard cuts** — Today every 8 photos there's a hard cut (no transition) at the chunk seam. Acceptable trade vs the alternative (mega-filtergraph that OOM'd). If users notice, switch to overlapping chunks (duplicate the last frame as the first of the next chunk and let xfade span — adds complexity).
3. **Aspect-ratio blur σ value** — `gblur=sigma=8` at 480×270 is current. Needs visual tuning against real test slideshows. Plan a tuning pass with the calibration photo sets used for duplicate detection thresholds.
4. **Music duration shorter than video** — `-stream_loop -1 -shortest` handles this (loops audio). User hears the same 30-sec loop 90 times if their MP3 is short. Probably fine; could warn if `music_duration < slideshow_duration / 6`.
5. **HEIC decode is single-threaded in libheif** — Parallelism in Stage 1 gives smaller wins on HEIC-heavy collections. May want to cap HEIC concurrency to 2-4. Measure before launch.

---

## 13. Incident log — design evolution

### Incident 1 (2026-05-25): 38-photo render OOM

**Symptom:** User ran their first real render with 38 mixed-aspect photos. FFmpeg failed with `[fc#0] Error while filtering: Cannot allocate memory`. The log showed all 38 inputs being held in the filter graph simultaneously, each with their split/scale/blur/overlay intermediates, then xfade-chained.

**Root cause:** Single complex filtergraph (the original design) does not scale to many inputs. The `gblur` at 1920×1080 per photo was particularly memory-hungry. Plus QSV adds GPU memory pressure on top.

**Fix:**
1. **Chunked rendering:** split into ≤8 photos per FFmpeg invocation, concatenate losslessly with `-c:v copy`. Each chunk's filtergraph is small enough to fit comfortably.
2. **Cheap blur:** scale background to 480×270 before `gblur`, then upscale to 1920×1080. Visually identical (blur destroys detail anyway) but ~16× cheaper in memory and CPU.
3. **Consistent staging color:** force `Magick.NET ColorType = TrueColor` on staged JPEGs so mixed grayscale/yuvj444p inputs don't trip swscaler.

**Regression test:** `RenderJob_ProducesValidMp4_AcrossMultipleChunks` renders 20 mixed-aspect synthetic photos (3 chunks + concat) to verify the pipeline works at this scale.

### Incident 2 (2026-05-25): LGPL FFmpeg missing libx264

**Symptom:** First end-to-end render test would have failed on any machine without a hardware H.264 encoder. The original encoder ladder fell back to `libx264`.

**Root cause:** BtbN's LGPL Win64 FFmpeg build is compiled with `--disable-libx264` (libx264 is GPL). For a commercial paid product we must use an LGPL build.

**Fix:** Switched the software fallback to `libopenh264` (Cisco-licensed, royalty-paid by Cisco, LGPL-compatible). Also added `h264_mf` (Windows Media Foundation) as an intermediate fallback — available universally on Windows 8+, often hardware-accelerated.

**Warning for future maintainers:** Do not switch back to libx264 without first switching to a GPL FFmpeg build.

### Incident 3 (2026-05-25): xfade offset math bug

**Symptom:** Initial implementation had `offset = i * S - T` instead of `offset = i * (S - T)`. Would have produced accelerating playback or stalls on > 2 photos.

**Root cause:** Transition's T seconds *overlap* (not add to) segment time, so each new segment contributes (S - T) of net new visible time, not S.

**Fix:** Caught during code review before first render. Documented inline in `FilterGraphBuilder.Build()` with a comment.

### Incident 7 (2026-05-26): PhotosPerChunk=50 regression

**Symptom:** Hypothesis was that consolidating to one chunk for the 50-photo trial workload would reduce FFmpeg process-startup overhead and beat the parallel-×2 baseline. Cold-cache measurement contradicted this:
- PhotosPerChunk=50, sequential, single chunk → Stage 2 = **149.50 s** (worse than the original 8-chunk sequential at 146.54 s)
- PhotosPerChunk=8, parallel×2, 7 chunks → Stage 2 = 109.26 s

**Root cause:** QSV filter graph state cost grows non-linearly past ~20 inputs on real photos. A single filtergraph with 50 inputs (50× split/scale/blur/overlay, 49 chained xfade nodes) is more expensive per-frame than 5–7 smaller graphs each with 8–12 inputs. The synthetic-photo test (320×240 solid color) failed to predict this because synthetic encode work is dominated by startup overhead, not by per-frame filter graph traversal — the relationship inverts at real photo resolutions.

**Fix:** Reverted to `PhotosPerChunk = 12` with parallel×2 (5 chunks, 3 parallel rounds for 50 photos). Stage 2 = 100.43 s in current cold-cache baseline.

**Warning for future maintainers:** Do not increase `PhotosPerChunk` past 12-15 without running a real 50-photo cold-cache timing test. Synthetic tests do not reliably predict real-photo behavior at the QSV filter graph state cost inflection.

### Incident 8 (2026-05-26): Stage 3 anomaly on first parallel run

**Symptom:** Stage 3 (concat + finalize) measured 20.28 s on the first run after compiling the parallel chunk implementation, then settled to ~12-13 s on every subsequent run. Reproduced sporadically on cold-cache reruns (20.31 s once, 12.86 s normally).

**Root cause:** Not definitively identified. Suspected candidates:
- Warm-path initialization in FFmpeg's mp4 muxer / `+faststart` moov-atom rewriter that pays a one-time cost per process / per-day
- Disk-cache state for the temp staging directory itself (separate from the source-photo cache)
- AAC encoder ramp-up if music is present

**Fix:** None made. Stage 3 stays well under the 15 s flag threshold on the vast majority of runs. Documented as known variance. If it starts exceeding 15 s consistently, instrument the Stage 3 FFmpeg invocation separately to isolate concat vs faststart cost.

### Decision: MaxLongerSide 2160 → 1920 (2026-05-26)
Cheap-blur renders the background source at 480×270. Output is 1080p (1920×1080). No consumer of the staged JPEG ever uses detail above 1920 px. Reduced `MaxLongerSide` constant in `StagingNormalizer.cs` from 2160 to 1920 — one-line change. ~16% fewer pixels per staged JPEG. Contributed to a measurable Stage 2 improvement (~6 s) likely via reduced FFmpeg per-input read cost.

### Decision: Conforming-photo skip path — NOT implemented, deferred (2026-05-26)
Discussed as one of four Option A/B/C/D candidates for Stage 1 optimization. The plan: for photos that already conform (JPEG + EXIF orientation = 1 + longer-side ≤ 1920 px), `File.Copy` source → staging and bypass the Magick.NET pipeline. Estimated savings: ~4,659 ms per eligible photo. Deferred because the test collection's source photos are essentially all > 1920 px, so the skip would fire for ~0% of photos in practice. May be revisited in V1.1 if a workflow surfaces with already-processed photos (e.g., re-rendering from a prior export). Documented here so a future contributor doesn't independently propose-and-defer the same optimization.

### Incident 4 (2026-05-26): Stage 1 performance on large-file collections
MaxLongerSide=1920 produced 472 ms/photo Resize time on a graduation collection with many 1440×1920 yuvj444p source files (894-photo, 689 ms/photo total Stage 1 wall). Reduced to 960 — staging at 960 px is sufficient because FFmpeg scales the foreground to fit 1920×1080 during render, and the blurred background is destroyed-detail regardless. Stage 1 parallelism also raised from 2 to `Math.Min(4, Math.Max(2, ProcessorCount-1))`. Expected combined impact: Stage 1 wall ≈ ¼ of pre-fix on a 4+ core machine (½ from Resize, ½ from added workers).

### Decision: Option C (HEIC concurrency cap) — NOT implemented, deferred (2026-05-26)
Stage 1 instrumentation revealed `FileRead` at 3,319 ms avg/photo. Initial inference from chunk stderr (`yuvj444p` chroma in ~60% of staging files) suggested HEIC-heavy sources, which would have made libheif's single-threaded global lock the bottleneck and a `SemaphoreSlim(2)` cap on HEIC decode the right fix. A diagnostic scan-only log line (`[DIAG] Photo format distribution: HEIC=X, JPEG=Y, PNG=Z`) was added to `ScanningViewModel` to validate this against ground truth. The DIAG line reported **HEIC=0, JPEG=48, PNG=2** — the 4:4:4 chroma was from high-quality DSLR JPEGs, not HEIC. Option C deferred. The DIAG instrumentation was removed after confirming the result. The HEIC cap can be added in ~10 minutes of code if a HEIC-heavy collection ever surfaces in beta.

### Decision: swscaler "deprecated pixel format" warnings are cosmetic, accepted (2026-05-27)
Each chunk's FFmpeg stderr emits ~35 `deprecated pixel format used, make sure you did set range correctly` warnings from libswscale. **Investigated and closed as cosmetic — output is correct.** Cause: libswscale is initialised with the deprecated `yuvj420p` enum that the mjpeg decoder emits for our staged JPEGs. It is NOT a range problem — FFmpeg already tags the inputs as `yuvj420p(pc, ...)` (full/PC range known) — so a `-color_range pc` input flag was tried and confirmed to have **zero effect** on the warning count. Eliminating them would require not feeding `yuvj*` frames to swscale at all (e.g. PNG staging), rejected for size/perf. Left as-is intentionally; documented inline in `RenderJob.BuildChunkArgs`. Don't re-investigate without new information. (Distinct from the Incident 1 `ColorType = TrueColor` fix, which addressed mixed grayscale/yuvj444p inputs, not this warning.)

---

## 14. Implementation Map

| Responsibility | File |
|---|---|
| Orchestration (3-stage pipeline) | `src/EasyPhotoShow.Core/Rendering/RenderJob.cs` |
| Stage 1 — staging normalize | `src/EasyPhotoShow.Core/Rendering/StagingNormalizer.cs` |
| Image decode + EXIF rotate + sRGB + TrueColor + JPEG write | `src/EasyPhotoShow.Core/Imaging/NormalizedBitmapLoader.cs` |
| Stage 2 — filtergraph builder | `src/EasyPhotoShow.Core/Rendering/FilterGraphBuilder.cs` |
| Encoder ladder + 1-frame probe | `src/EasyPhotoShow.Core/Rendering/EncoderProbe.cs` |
| FFmpeg binary discovery | `src/EasyPhotoShow.Core/Rendering/FFmpegEnvironment.cs` |
| FFmpeg progress event parsing | `src/EasyPhotoShow.Core/Rendering/ProgressParser.cs` |
| Music preset resolution (Assets/Music/) | `src/EasyPhotoShow.Core/Rendering/MusicResolver.cs` |
| MP3 duration probe via ffprobe | `src/EasyPhotoShow.Core/Rendering/MusicMetadataProbe.cs` |
| Render error classification + RenderException | `src/EasyPhotoShow.Core/Rendering/RenderException.cs` |
| Render progress data shape | `src/EasyPhotoShow.Core/Models/RenderProgress.cs` |
| Rendering screen ViewModel | `src/EasyPhotoShow.App/ViewModels/RenderingViewModel.cs` |
| Rendering screen XAML | `src/EasyPhotoShow.App/Views/RenderingView.xaml` |
| End-to-end integration test | `tests/EasyPhotoShow.Core.Tests/RenderJobIntegrationTests.cs` |
