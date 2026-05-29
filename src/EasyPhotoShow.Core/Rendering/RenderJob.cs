using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyPhotoShow.Core.Imaging;
using EasyPhotoShow.Core.Models;

namespace EasyPhotoShow.Core.Rendering;

public sealed class RenderJob
{
    // Photos per FFmpeg chunk invocation. 12 is the empirical sweet spot — large enough to
    // amortize FFmpeg startup across fewer process launches, small enough that each filter
    // graph stays responsive (QSV holds graph state per invocation; very large graphs slow
    // per-chunk encode disproportionately). For a 50-photo render this produces 5 chunks.
    // Performance history (50 real photos, QSV):
    //   PhotosPerChunk=8  sequential  → Stage 2 = 146.54 s
    //   PhotosPerChunk=8  parallel×2  → Stage 2 = 109.26 s  ← best measured result
    //   PhotosPerChunk=50 sequential  → Stage 2 = 149.50 s  (single mega-graph was slower)
    // RunAsync applies a runtime fallback to LowMemoryChunkSize if available memory < 2 GB.
    // Tested PhotosPerChunk=20 on 2026-05-27 (988 real photos, QSV, after MaxLongerSide
    // dropped to 960 made staged files ~4× smaller). Hypothesis was that smaller staged
    // files would let us amortize FFmpeg startup across fewer chunks. Result: Stage 2
    // REGRESSED from 959.6 s → 1009.6 s (+5.2%). Per-photo encode rate went from 1.83
    // s/photo at 12 to 1.95 s/photo at 20 — the QSV filter-graph cost grows non-linearly
    // past ~12 inputs even when each input is small. Stage 3 did get 22% faster from
    // having 50 chunks to concat instead of 83, but the +50 s Stage 2 cost more than wiped
    // out the −29 s Stage 3 saving (net +34 s on the full render). 12 remains the
    // validated ceiling for QSV; do not raise without re-measuring on real photos.
    private const int PhotosPerChunk = 12;
    private const int LowMemoryChunkSize = 8;

    // Parallel chunk rendering runs 2 chunks simultaneously via Parallel.ForEachAsync.
    // - 2 is the sweet spot: ~halves Stage 2 wall time while staying inside hardware H.264
    //   encoder session limits (NVENC consumer caps 3-5, QSV/AMF/MF 16+).
    // - aggregateLock + chunkFractions[] + photosDonePerChunk[] keep the overall progress
    //   fraction monotonically increasing as a SUM of all chunks' contributions; without
    //   this, parallel chunks would visibly leapfrog each other in the UI.
    // - LogLock serializes stderr appends so chunk-N's stderr doesn't interleave with
    //   chunk-M's in the diagnostic log file.
    // - Per-chunk cancellation is automatic via Parallel.ForEachAsync's iteration token —
    //   one thrown exception cancels siblings, and RunChunkFFmpegAsync's ct.Register kills
    //   the still-running FFmpeg process.
    // Was previously reverted when PhotosPerChunk briefly went to 50 (single chunk =
    // zero parallel benefit). Restored when PhotosPerChunk landed at 12 (5 chunks for the
    // benchmark workload). See class-level perf history comment above for the data.
    private static readonly object LogLock = new();

    public async Task RunAsync(
        SlideshowSettings settings,
        IReadOnlyList<Photo> orderedPhotos,
        IProgress<RenderProgress>? progress,
        CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var stagingDir = Path.Combine(Path.GetTempPath(), "EasyPhotoShow", "staging", sessionId);
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyPhotoShow", "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{sessionId[..8]}.log");
        var partialOutput = settings.OutputPath + ".part.mp4";

        try
        {
            // Diagnostic timing — not user-facing. Written to logPath at end of run so the
            // line ordering in the log reads naturally (FFmpeg stderr first, then timings).
            // We capture local-time DateTimes at stage boundaries instead of using Stopwatch,
            // so the displayed seconds for each stage match the (end - start) of the timestamp
            // columns exactly — no hidden gap between Stage N stop and Stage N+1 start.
            var renderStart = DateTime.Now;
            DateTime stage1End = renderStart, stage2End = renderStart, stage3End = renderStart;

            File.AppendAllText(logPath,
                $"[timing] RenderJob.RunAsync started at {renderStart:HH:mm:ss.fff}, " +
                $"{orderedPhotos.Count} photos, secondsPerPhoto={settings.SecondsPerPhoto:N2}\n");

            // Memory safety guard. If available memory is below 2 GB, drop chunk size to
            // LowMemoryChunkSize (8) so each FFmpeg invocation holds a smaller filter graph.
            // The default PhotosPerChunk (50) is tuned for the cheap-blur memory profile and
            // typical desktop systems; this fallback covers older / memory-tight machines.
            var memInfo = GC.GetGCMemoryInfo();
            int safeChunkSize = memInfo.TotalAvailableMemoryBytes < 2L * 1024 * 1024 * 1024
                ? LowMemoryChunkSize
                : PhotosPerChunk;
            if (safeChunkSize != PhotosPerChunk)
            {
                File.AppendAllText(logPath,
                    "[WARNING] Low memory (<2GB available). " +
                    $"Using PhotosPerChunk={LowMemoryChunkSize} for stability.\n");
            }

            ReportStage(progress, RenderStage.PreparingPhotos, 0, 0, orderedPhotos.Count, null);

            // Opener/closer bookends. Generated BEFORE Stage 1 and BEFORE StagingTimings.Reset()
            // so the [TIMING-S1] block keeps measuring only the user's photos. A text card is
            // rendered by TitleCardGenerator; a custom image is normalized through the same
            // WriteJpeg path as any photo (honors EXIF orientation + resize). The resulting
            // staged files are pinned to the first/last positions after Stage 1 — they never
            // pass through ordering. Runs on this background render task, off the UI thread.
            Directory.CreateDirectory(stagingDir);
            var openerStaged = await PrepareBookendAsync(
                settings.OpenerSlide, Path.Combine(stagingDir, "_opener.jpg"), ct);
            var closerStaged = await PrepareBookendAsync(
                settings.CloserSlide, Path.Combine(stagingDir, "_closer.jpg"), ct);

            // Front-load the heaviest files into the first chunk. QSV is slowest on its
            // very first frames (encoder warm-up + larger filter inputs), so processing
            // the biggest files first produces a pessimistic early ETA that visibly drops
            // as subsequent chunks complete faster — building user trust rather than
            // breaking it with an estimate that climbs. Sorts only the first chunk's worth;
            // the rest preserves Best Mix order so the slideshow's variety contract holds.
            //
            // Uses source Photo.FileSize (populated in Phase 1 scanning) as the proxy for
            // staging size — strongly correlated and avoids a chicken-and-egg with staging.
            var reorderedPhotos = ReorderFirstChunkBySize(orderedPhotos, safeChunkSize);

            // Reset per-phase Stage 1 accumulators so this run's [TIMING-S1] breakdown
            // is isolated. Captured + logged below as soon as the normalizer returns.
            StagingTimings.Reset();

            var normalizer = new StagingNormalizer();
            var stagedFiles = await Task.Run(() => normalizer.Normalize(
                reorderedPhotos,
                stagingDir,
                new System.Progress<StagingNormalizer.Progress>(p =>
                {
                    var frac = p.Total == 0 ? 0 : (double)p.Processed / p.Total * 0.20;
                    ReportStage(progress, RenderStage.PreparingPhotos, frac, p.Processed, p.Total, null);
                }),
                ct), ct);

            stage1End = DateTime.Now;

            // Per-phase Stage 1 breakdown. Aggregated across the parallel workers, so the
            // sum will exceed wall-clock Stage 1 time by a factor proportional to the worker
            // count. The per-photo avg numbers are the more useful comparison.
            File.AppendAllText(logPath, FormatStagingTimingsBlock(reorderedPhotos.Count));

            // Pin the bookends: opener first, closer last. Done here (after Stage 1, after
            // any ordering done by the caller) so they are never subject to reordering.
            var stagedWithBookends = new List<string>(stagedFiles.Count + 2);
            if (openerStaged is not null) stagedWithBookends.Add(openerStaged);
            stagedWithBookends.AddRange(stagedFiles);
            if (closerStaged is not null) stagedWithBookends.Add(closerStaged);

            ReportStage(progress, RenderStage.CreatingSlideshow, 0.20, stagedWithBookends.Count, stagedWithBookends.Count, null);

            var encoder = await Task.Run(() => EncoderProbe.Pick(ct), ct);
            var mp3 = MusicResolver.ResolveMp3Path(settings.Music);

            var chunkPaths = await RenderChunksAsync(
                stagedWithBookends, settings, encoder, safeChunkSize, stagingDir, logPath, progress, ct);

            stage2End = DateTime.Now;

            // Concat + (optional) music mixing. With music, present this as "Adding music";
            // otherwise as "Finalizing slideshow" so the user sees a stage that matches what's
            // actually happening rather than a confusing audio-related label with no audio.
            var concatStage = mp3 is not null ? RenderStage.AddingMusic : RenderStage.FinalizingSlideshow;
            ReportStage(progress, concatStage, 0.94, orderedPhotos.Count, orderedPhotos.Count, null);

            // Total slideshow runtime, computed analytically (concat is a lossless append of
            // the chunks). Each chunk of c photos lasts c*S - (c-1)*T; summed across all chunks
            // that is n*S - (n - chunkCount)*T, since transitions only occur within chunks
            // (chunk boundaries are hard cuts). Used to place the music fade-out at the end.
            double videoDurationSeconds = stagedWithBookends.Count * settings.SecondsPerPhoto
                - (stagedWithBookends.Count - chunkPaths.Count) * FilterGraphBuilder.TransitionSeconds;

            if (chunkPaths.Count == 1 && mp3 is null)
            {
                File.Move(chunkPaths[0], partialOutput);
            }
            else
            {
                await ConcatChunksAsync(chunkPaths, mp3, partialOutput, stagingDir, logPath, videoDurationSeconds, ct);
            }

            // If music was being added, the final move/validate is still "Finalizing" — show it briefly.
            if (mp3 is not null)
                ReportStage(progress, RenderStage.FinalizingSlideshow, 0.97, orderedPhotos.Count, orderedPhotos.Count, null);

            if (!File.Exists(partialOutput))
                throw new RenderException(RenderFailureKind.Unknown, "Something went wrong while finishing your slideshow. Please try again.", logPath);
            if (new FileInfo(partialOutput).Length < 1024)
                throw new RenderException(RenderFailureKind.Unknown, "Something went wrong while finishing your slideshow. Please try again.", logPath);

            if (File.Exists(settings.OutputPath))
                File.Delete(settings.OutputPath);
            File.Move(partialOutput, settings.OutputPath);

            stage3End = DateTime.Now;

            // Single block at end so the three stage times read together in the log.
            // Each stage's seconds = (stageEnd - stageStart) where stageStart = previous
            // stage's end timestamp. Per spec: Stage N end is identical to Stage N+1 start;
            // TOTAL uses (stage3End - renderStart), not the sum of stage durations.
            var s1Sec = (stage1End - renderStart).TotalSeconds;
            var s2Sec = (stage2End - stage1End).TotalSeconds;
            var s3Sec = (stage3End - stage2End).TotalSeconds;
            var totalSec = (stage3End - renderStart).TotalSeconds;
            // Format note: label padded to 27 cols (longest is "Stage 3 (Concat/finalize):" at
            // 26 chars), value right-aligned in 9 cols with N2 (fits up to 99,999.99 s — over a day).
            File.AppendAllText(logPath,
                $"\n[timing] {"Stage 1 (Normalize):",-27}{s1Sec,9:N2} s   {renderStart:HH:mm:ss} → {stage1End:HH:mm:ss}   ({reorderedPhotos.Count} photos)\n" +
                $"[timing] {"Stage 2 (Chunked render):",-27}{s2Sec,9:N2} s   {stage1End:HH:mm:ss} → {stage2End:HH:mm:ss}   (encoder={encoder})\n" +
                $"[timing] {"Stage 3 (Concat/finalize):",-27}{s3Sec,9:N2} s   {stage2End:HH:mm:ss} → {stage3End:HH:mm:ss}\n" +
                $"[timing] {"TOTAL:",-27}{totalSec,9:N2} s   {renderStart:HH:mm:ss} → {stage3End:HH:mm:ss}\n");

            ReportStage(progress, RenderStage.Complete, 1.0, reorderedPhotos.Count, reorderedPhotos.Count, TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(partialOutput);
            throw;
        }
        catch (RenderException)
        {
            SafeDelete(partialOutput);
            throw;
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, "\n\n[unhandled]\n" + ex);
            SafeDelete(partialOutput);
            throw new RenderException(RenderFailureKind.Unknown, "Something unexpected happened. Please try again.", logPath);
        }
        finally
        {
            SafeDeleteDirectory(stagingDir);
        }
    }

    private async Task<List<string>> RenderChunksAsync(
        IReadOnlyList<string> stagedFiles,
        SlideshowSettings settings,
        VideoEncoder encoder,
        int chunkSize,
        string stagingDir,
        string logPath,
        IProgress<RenderProgress>? progress,
        CancellationToken ct)
    {
        var photoCount = stagedFiles.Count;
        var totalChunks = (int)Math.Ceiling((double)photoCount / chunkSize);
        var chunkPaths = new string[totalChunks];

        // Per-chunk progress tracking, aggregated under a single lock so the overall
        // progress fraction is the SUM of all chunks' contributions — not whichever
        // chunk reported most recently. Without this, parallel chunk reports would
        // visibly jump back and forth as different chunks reported their local fractions.
        var chunkFractions = new double[totalChunks];
        var photosDonePerChunk = new int[totalChunks];
        var aggregateLock = new object();
        var spanPerChunk = 0.74 / totalChunks;

        // Sliding-window rate samples for ETA. Previously the ETA was a cumulative average
        // from t=0, which produced a climbing display in practice: QSV session warm-up and
        // parallel-pipeline initialization make the first 1-2 chunks faster than steady
        // state, so the early "elapsed / done" rate was optimistically high. As the true
        // steady-state pace asserted itself the ETA visibly climbed (6→11→16→17 min on a
        // ~2,729-photo render), which looks broken even though the math is just correcting.
        // Sliding window keeps only the most recent EtaWindowPhotos samples so the rate
        // reflects current speed. ETA is also suppressed until both a minimum % of photos
        // AND a minimum chunk count have completed, so the first displayed estimate is
        // computed on a stable rate rather than a single-chunk anomaly.
        const int EtaWindowPhotos = 75;
        const double EtaMinFractionForFirstEstimate = 0.05; // ≥5% of total photos done
        const int EtaMinChunksForFirstEstimate = 2;          // AND ≥2 chunks complete
        var rateSamples = new List<(DateTime TimeUtc, int Done)>(capacity: 256);

        void ReportOverall()
        {
            if (progress is null) return;
            double overallSum;
            int totalPhotosDone;
            int chunksComplete;
            (DateTime TimeUtc, int Done) windowStart;
            DateTime nowUtc = DateTime.UtcNow;
            lock (aggregateLock)
            {
                overallSum = 0;
                totalPhotosDone = 0;
                chunksComplete = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    overallSum += chunkFractions[i];
                    totalPhotosDone += photosDonePerChunk[i];
                    if (chunkFractions[i] >= 1.0) chunksComplete++;
                }
                // Append this sample, then trim everything outside the EtaWindowPhotos
                // window measured from the back. Always keep at least one sample.
                rateSamples.Add((nowUtc, totalPhotosDone));
                while (rateSamples.Count > 1 &&
                       totalPhotosDone - rateSamples[0].Done > EtaWindowPhotos)
                {
                    rateSamples.RemoveAt(0);
                }
                windowStart = rateSamples[0];
            }
            var overall = Math.Min(0.94, 0.20 + spanPerChunk * overallSum);

            TimeSpan? eta = null;
            int minPhotosForEta = (int)Math.Ceiling(photoCount * EtaMinFractionForFirstEstimate);
            bool thresholdMet = totalPhotosDone >= minPhotosForEta
                                && chunksComplete >= EtaMinChunksForFirstEstimate;
            if (thresholdMet)
            {
                int windowDelta = totalPhotosDone - windowStart.Done;
                double windowSeconds = (nowUtc - windowStart.TimeUtc).TotalSeconds;
                if (windowDelta > 0 && windowSeconds > 0.1)
                {
                    double photosPerSec = windowDelta / windowSeconds;
                    int remainingPhotos = Math.Max(0, photoCount - totalPhotosDone);
                    double remainingSec = remainingPhotos / photosPerSec;
                    var rounded = Math.Ceiling(remainingSec / 30.0) * 30.0;
                    eta = TimeSpan.FromSeconds(rounded);
                }
            }

            progress.Report(new RenderProgress
            {
                Stage = RenderStage.CreatingSlideshow,
                FractionComplete = overall,
                PhotosProcessed = Math.Min(photoCount, totalPhotosDone),
                PhotosTotal = photoCount,
                EstimatedTimeRemaining = eta
            });
        }

        // Parallel.ForEachAsync: bounded concurrency (2 chunks simultaneously — see class
        // header for rationale and hardware-encoder session limits). Cooperative cancellation:
        // if any iteration throws, the framework cancels the sibling iterations' tokens,
        // which causes RunChunkFFmpegAsync to kill its FFmpeg process via ct.Register and bail.
        // First thrown exception bubbles out at the end.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, totalChunks),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = ct
            },
            async (chunkIdx, lct) =>
            {
                int start = chunkIdx * chunkSize;
                int count = Math.Min(chunkSize, photoCount - start);
                var chunkFiles = stagedFiles.Skip(start).Take(count).ToList();
                var chunkOutput = Path.Combine(stagingDir, $"chunk_{chunkIdx:D3}.mp4");

                var graph = FilterGraphBuilder.Build(
                    count,
                    settings.SecondsPerPhoto,
                    settings.Transition,
                    new Random(StableSeed(chunkFiles)));

                var args = BuildChunkArgs(chunkFiles, settings.SecondsPerPhoto, graph, encoder, chunkOutput);

                await RunChunkFFmpegAsync(
                    args,
                    chunkIdx,
                    count,
                    settings.SecondsPerPhoto,
                    logPath,
                    lct,
                    (localFraction, photosDoneInChunk) =>
                    {
                        lock (aggregateLock)
                        {
                            chunkFractions[chunkIdx] = localFraction;
                            photosDonePerChunk[chunkIdx] = photosDoneInChunk;
                        }
                        ReportOverall();
                    });

                if (!File.Exists(chunkOutput))
                    throw new RenderException(RenderFailureKind.Unknown,
                        "Something went wrong while finishing your slideshow. Please try again.", logPath);

                chunkPaths[chunkIdx] = chunkOutput;

                // Belt-and-suspenders: the last FFmpeg progress event may not land exactly at
                // 100% (frame rounding). Force the chunk's contribution to 1.0 so overall
                // progress reaches 0.94 cleanly when all chunks finish.
                lock (aggregateLock)
                {
                    chunkFractions[chunkIdx] = 1.0;
                    photosDonePerChunk[chunkIdx] = count;
                }
                ReportOverall();
            });

        return chunkPaths.ToList();
    }

    // Gentle music envelope so the track doesn't start or stop abruptly. Clamped for short
    // slideshows so the fades never overlap or exceed the runtime (see ClampedFade).
    private const double MusicFadeInSeconds = 2.0;
    private const double MusicFadeOutSeconds = 3.0;

    private static async Task ConcatChunksAsync(
        IReadOnlyList<string> chunkPaths,
        string? mp3Path,
        string outputPath,
        string stagingDir,
        string logPath,
        double videoDurationSeconds,
        CancellationToken ct)
    {
        var listPath = Path.Combine(stagingDir, "concat.txt");
        var sb = new StringBuilder();
        foreach (var p in chunkPaths)
            sb.AppendLine($"file '{p.Replace("'", "'\\''")}'");
        await File.WriteAllTextAsync(listPath, sb.ToString(), ct);

        var ci = CultureInfo.InvariantCulture;
        var args = new StringBuilder();
        args.Append("-hide_banner -y -nostats ");
        args.AppendFormat(ci, "-f concat -safe 0 -i \"{0}\" ", listPath);
        if (mp3Path is not null)
        {
            // Loop the track to cover the full video, then fade it in at the start and out at
            // the end. afade out is placed at (duration - fadeOut); -shortest trims the looped
            // audio to the video length so the fade-out lands exactly on the final frames.
            var (fadeIn, fadeOut) = ClampedFade(videoDurationSeconds);
            args.AppendFormat(ci, "-stream_loop -1 -i \"{0}\" ", mp3Path);
            args.Append("-map 0:v -map 1:a -c:v copy ");
            if (fadeIn > 0 || fadeOut > 0)
            {
                double fadeOutStart = Math.Max(0, videoDurationSeconds - fadeOut);
                args.AppendFormat(ci,
                    "-af \"afade=t=in:st=0:d={0:0.###},afade=t=out:st={1:0.###}:d={2:0.###}\" ",
                    fadeIn, fadeOutStart, fadeOut);
            }
            args.Append("-c:a aac -b:a 192k -ar 48000 -shortest ");
        }
        else
        {
            args.Append("-map 0:v -c:v copy ");
        }
        args.AppendFormat(ci, "-movflags +faststart \"{0}\"", outputPath);

        var psi = new ProcessStartInfo(FFmpegEnvironment.FFmpegPath, args.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new RenderException(RenderFailureKind.Unknown, "Could not start the rendering process.", logPath);

        var stderrSb = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                stderrSb.AppendLine(line);
        });
        var stdoutTask = Task.Run(async () => { await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false); });

        var killReg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        finally { killReg.Dispose(); }

        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            var stderr = stderrSb.ToString();
            File.AppendAllText(logPath, "\n\n[concat stderr]\n" + stderr);
            var kind = ClassifyFailure(stderr);
            throw new RenderException(kind, MessageForFailure(kind), logPath);
        }
    }

    private static int StableSeed(IReadOnlyList<string> files)
    {
        unchecked
        {
            int seed = 17;
            foreach (var f in files)
                seed = seed * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(f);
            return seed;
        }
    }

    private static string BuildChunkArgs(
        IReadOnlyList<string> stagedFiles,
        double secondsPerPhoto,
        string graph,
        VideoEncoder encoder,
        string outputPath)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("-hide_banner -y -nostats -progress pipe:1 ");

        // NOTE on swscaler "deprecated pixel format used" warnings (~35/chunk): these come
        // from libswscale being initialised with the deprecated yuvj420p enum that the mjpeg
        // decoder emits for our staged JPEGs. It is NOT a range problem — FFmpeg already tags
        // the inputs as yuvj420p(pc, ...) i.e. full/PC range is known — so a -color_range pc
        // input flag was tried (2026-05-27) and confirmed to have zero effect on the warning
        // count. The warnings are cosmetic (output is correct); truly eliminating them would
        // require not feeding yuvj* frames to swscale at all (e.g. PNG staging), which we
        // rejected for size/perf. Left as-is intentionally.
        foreach (var f in stagedFiles)
            sb.AppendFormat(ci, "-loop 1 -t {0} -i \"{1}\" ", secondsPerPhoto.ToString(ci), f);

        sb.AppendFormat(ci, "-filter_complex \"{0}\" -map \"[vout]\" -an ", graph);

        sb.Append(EncoderProbe.ToFFmpegArgs(encoder));
        sb.AppendFormat(ci, " -r {0} \"{1}\"", FilterGraphBuilder.FrameRate, outputPath);
        return sb.ToString();
    }

    // Runs one chunk's FFmpeg invocation. Reports raw chunk-local progress (fraction 0..1
    // and per-chunk photos-done) via a callback; the caller is responsible for aggregating
    // across parallel chunks and reporting overall progress.
    //
    // Stderr is buffered to memory and appended to the shared log file in one locked write
    // at the end of the chunk, so parallel chunks don't interleave their stderr lines.
    private static async Task RunChunkFFmpegAsync(
        string args,
        int chunkIdx,
        int photosInChunk,
        double secondsPerPhoto,
        string logPath,
        CancellationToken ct,
        Action<double, int> onProgress)
    {
        var chunkDurationSeconds = photosInChunk * secondsPerPhoto
                                   - Math.Max(0, photosInChunk - 1) * FilterGraphBuilder.TransitionSeconds;

        var psi = new ProcessStartInfo(FFmpegEnvironment.FFmpegPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new RenderException(RenderFailureKind.Unknown, "Could not start the rendering process.", logPath);

        var parser = new ProgressParser();

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                parser.Feed(line);
                if (parser.OutTimeSeconds > 0)
                {
                    var localFraction = Math.Min(1.0, parser.OutTimeSeconds / Math.Max(0.001, chunkDurationSeconds));
                    var photosDone = Math.Min(photosInChunk, (int)(parser.OutTimeSeconds / secondsPerPhoto) + 1);
                    onProgress(localFraction, photosDone);
                }
            }
        });

        var stderrSb = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                stderrSb.AppendLine(line);
        });

        var killReg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });

        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        finally { killReg.Dispose(); }

        // Single locked log append per chunk so parallel chunks' stderr doesn't interleave.
        var stderrText = stderrSb.ToString();
        if (!string.IsNullOrEmpty(stderrText))
        {
            lock (LogLock)
            {
                File.AppendAllText(logPath,
                    $"\n[chunk {chunkIdx:D3} stderr]\n{stderrText}");
            }
        }

        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
        {
            var kind = ClassifyFailure(stderrText);
            throw new RenderException(kind, MessageForFailure(kind), logPath);
        }
    }

    private static string MessageForFailure(RenderFailureKind kind) => kind switch
    {
        RenderFailureKind.DiskFull => "There's not enough space on the drive to finish your slideshow. Please free up space and try again.",
        RenderFailureKind.OutputUnwritable => "EasyPhotoShow couldn't save to that location. Please choose a different folder.",
        RenderFailureKind.SourceUnavailable => "One of your photos became unavailable during rendering. Please check that all drives are connected and try again.",
        RenderFailureKind.EncoderFailure => "Slideshow rendering hit a snag. Please try again.",
        _ => "Something unexpected happened. Please try again."
    };

    private static RenderFailureKind ClassifyFailure(string stderr)
    {
        if (stderr.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            return RenderFailureKind.DiskFull;
        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            return RenderFailureKind.OutputUnwritable;
        if (stderr.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
            return RenderFailureKind.SourceUnavailable;
        if (stderr.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Error initializing", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Could not find encoder", StringComparison.OrdinalIgnoreCase))
            return RenderFailureKind.EncoderFailure;
        return RenderFailureKind.Unknown;
    }

    private static void ReportStage(IProgress<RenderProgress>? progress, RenderStage stage, double fraction, int processed, int total, TimeSpan? eta)
    {
        progress?.Report(new RenderProgress
        {
            Stage = stage,
            FractionComplete = fraction,
            PhotosProcessed = processed,
            PhotosTotal = total,
            EstimatedTimeRemaining = eta
        });
    }

    private static string FormatStagingTimingsBlock(int photoCount)
    {
        var snap = StagingTimings.Read();
        long tps = Stopwatch.Frequency;
        static long ToMs(long ticks, long tps) => (long)(ticks * 1000.0 / tps);
        int safeCount = Math.Max(1, photoCount);
        long fr = ToMs(snap.FileReadTicks, tps);
        long ao = ToMs(snap.AutoOrientTicks, tps);
        long cs = ToMs(snap.ColorSpaceTicks, tps);
        long ct = ToMs(snap.ColorTypeTicks, tps);
        long rs = ToMs(snap.ResizeTicks, tps);
        long sw = ToMs(snap.StripWriteTicks, tps);
        long total = fr + ao + cs + ct + rs + sw;

        return
            $"[TIMING-S1] FileRead:   {fr,8} ms total ({fr / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] AutoOrient: {ao,8} ms total ({ao / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] ColorSpace: {cs,8} ms total ({cs / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] ColorType:  {ct,8} ms total ({ct / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] Resize:     {rs,8} ms total ({rs / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] StripWrite: {sw,8} ms total ({sw / safeCount,6} ms avg/photo)\n" +
            $"[TIMING-S1] TOTAL:      {total,8} ms        ({total / safeCount,6} ms avg/photo)\n";
    }

    // Reorders the first `chunkSize` photos by FileSize descending, leaving everything
    // after the first chunk in its original Best Mix order. The largest files going first
    // makes the initial ETA pessimistic and improves over time — far better UX than an
    // optimistic ETA that climbs as the encoder hits heavier files later.
    private static IReadOnlyList<Photo> ReorderFirstChunkBySize(
        IReadOnlyList<Photo> orderedPhotos, int chunkSize)
    {
        if (orderedPhotos.Count <= 1 || chunkSize <= 1) return orderedPhotos;
        var firstChunk = orderedPhotos
            .Take(chunkSize)
            .OrderByDescending(p => p.FileSize)
            .ToList();
        if (orderedPhotos.Count <= chunkSize) return firstChunk;
        var rest = orderedPhotos.Skip(chunkSize).ToList();
        return firstChunk.Concat(rest).ToList();
    }

    // (fadeIn, fadeOut) seconds, each capped at a quarter of the runtime so they never overlap
    // or exceed the slideshow length — combined they take at most half, always leaving steady
    // music in the middle. A 2-photo / very short slideshow gets proportionally shorter fades.
    private static (double FadeIn, double FadeOut) ClampedFade(double durationSeconds)
    {
        if (durationSeconds <= 0) return (0, 0);
        double cap = durationSeconds * 0.25;
        return (Math.Min(MusicFadeInSeconds, cap), Math.Min(MusicFadeOutSeconds, cap));
    }

    // Produces the staged JPEG for an opener/closer bookend, or null when the bookend is off.
    // Text card → TitleCardGenerator; custom image → the same normalize-and-write path as a
    // photo (EXIF orientation + resize + TrueColor). Runs on a background thread.
    private static async Task<string?> PrepareBookendAsync(SlideContent? content, string destPath, CancellationToken ct)
    {
        if (content is null) return null;
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            switch (content)
            {
                case TextCardSlide textCard:
                    TitleCardGenerator.Generate(textCard, destPath);
                    break;
                case CustomImageSlide image:
                    NormalizedBitmapLoader.WriteJpeg(
                        image.ImagePath, destPath,
                        StagingNormalizer.MaxLongerSide, StagingNormalizer.JpegQuality);
                    break;
            }
        }, ct).ConfigureAwait(false);
        return destPath;
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
