# EasyPhotoShow V1 — Duplicate Detection Design

## Status
**Implemented.** Last reviewed: 2026-05-28.

Pipeline lives in `src/EasyPhotoShow.Core/DuplicateDetection/` with image decoding in `src/EasyPhotoShow.Core/Imaging/`. UI binding in `src/EasyPhotoShow.App/ViewModels/DuplicateReviewViewModel.cs` and view in `src/EasyPhotoShow.App/Views/DuplicateReviewView.xaml`.

Tests cover dHash determinism, union-find grouping, group classification (ExactOnly vs HasVisualComponent), exact-group auto-resolution, and the recommended-photo heuristic (see `tests/EasyPhotoShow.Core.Tests/`).

The scan is instrumented end-to-end with a `[TIMING-SCAN]` log block (§10.1). The **scanning screen is text-only** — a running "N exact duplicate files handled so far" count, no thumbnail preview cards (removed 2026-05-28 after they displayed as blank grey boxes on a large scan; see `06_Code_Handoff.md` §11). Thumbnails remain only on the duplicate **review** screen, where they're load-bearing for the user's decision.

---

## 1. Goals

The duplicate detection system must:

- Catch obvious duplicates reliably (same file copied twice, same photo at different resolutions, near-identical burst shots)
- Never produce false positives that destroy user trust
- Run on 1,000+ photos without freezing the UI
- Present results in plain language with no scores, percentages, or technical jargon
- Honor the V1 promise: **never deletes files** — only moves user-approved duplicates to `PotentialDuplicates`

---

## 2. Pipeline Overview

Three-phase tiered approach. Each phase is cheaper to skip than to run, so we run them in order of cost.

```
Phase 1: Index           (filesystem walk + EXIF read)
Phase 2: Exact match     (SHA-256 hash of files with matching size)
Phase 3: Perceptual      (dHash of all images, grouped by similarity)
```

Phases 1 and 3 run on every scan. Phase 2 only runs on files whose size matches another file (small subset).

**Code entry point:** `DuplicateDetector.Detect(IReadOnlyList<Photo>, ...)` in `src/EasyPhotoShow.Core/DuplicateDetection/DuplicateDetector.cs`.

---

## 3. Phase 1 — Index

For each supported image file in selected folders:

| Field | Source | Cost |
|---|---|---|
| `Path` | filesystem | free |
| `FileSize` | filesystem | free |
| `FileName` | filesystem | free |
| `CaptureTime` | EXIF `DateTimeOriginal`, fallback to file modified time | cheap |
| `VisualWidth`, `VisualHeight` | EXIF, fallback to image header | cheap |
| `Format` (JPEG/PNG/Heic) | extension classification | free |

**Implementation:** `FolderScanner.Scan()` in `src/EasyPhotoShow.Core/Scanning/FolderScanner.cs` walks supplied folders recursively with `IgnoreInaccessible=true`, skipping anything inside an existing `PotentialDuplicates/` subfolder (so re-scans don't duplicate-process previously-moved files). EXIF is read via `MetadataExtractor`. Unsupported-but-image-like extensions (GIF, TIFF, RAW, etc.) are counted into `ScanResult.UnsupportedFileCount` for the "N unsupported files were skipped" message.

**Output:** in-memory `ScanResult` with `Photos` and `UnsupportedFileCount`.

---

## 4. Phase 2 — Exact Match

**Goal:** catch identical files (same image copied to two folders, downloaded twice, etc.).

**Algorithm:**
1. Group photos by `FileSize`. Discard singleton groups.
2. For each multi-file group, compute SHA-256 of file bytes.
3. Files with matching SHA-256 → **Exact Duplicate** relationship.

**Why SHA-256 over xxHash:** SHA-256 is fast enough (~500 MB/s on modern CPUs) and eliminates any collision risk. xxHash is faster but introduces a non-zero collision probability that's hard to explain to non-technical users if it ever matters.

**Code:** `DuplicateDetector.ComputeShaHashesForSizeMatches()` — only hashes files with size collisions, attaches `Sha256` to the `Photo` record (which is a C# `record`, so `with { Sha256 = ... }` returns a new instance).

**Performance:** Only hashes files with a size-collision partner. In a typical 1,000-photo collection with ~5% true duplicates, this is well under 100 file hashes.

---

## 5. Phase 3 — Perceptual Similarity

**Goal:** catch visually-identical-but-not-byte-identical photos: burst-mode shots, re-exports, resizes, mild edits, rotations.

**Algorithm — dHash (difference hash):**

1. Decode image at 9×8 grayscale (using EXIF-rotation-corrected pixels via `Magick.NET.AutoOrient()`)
2. Compute 64-bit hash: each bit = "is this pixel brighter than the one to its right?"
3. Compare hashes pairwise via Hamming distance (`System.Numerics.BitOperations.PopCount(a ^ b)`)
4. Photos with distance ≤ **8 bits** (`DHash.SimilarityThresholdBits`) → **Visual Duplicate** relationship. Threshold calibrated against a real-world test collection; same-scene photos with significantly different lighting or angle may exceed this threshold and require a complementary perceptual hash for reliable detection.

**Code:** `DHash.Compute()` in `src/EasyPhotoShow.Core/Imaging/DHash.cs`. Runs in parallel via `Parallel.For` with progress reporting.

**Why dHash over pHash:**
- Slightly more robust against gamma/brightness shifts (common in re-edits)
- Cheaper to compute (no DCT)
- Same 64-bit output size
- Industry-standard choice for this use case

**Critical detail — orientation:** Compute the hash on the *rotation-corrected* image, not raw pixels. Otherwise a photo and its EXIF-rotated copy will hash differently despite being visually identical. Magick.NET's `AutoOrient()` handles all 8 EXIF orientation values exhaustively.

**Pairwise comparison cost:**
- Naive O(n²) is fine up to ~5,000 photos (~12M comparisons, each a single XOR + popcount)
- For larger sets, switch to a BK-tree on Hamming distance to reduce to O(n log n) average
- V1: ships naive (see `DuplicateDetector.BuildGroups`); BK-tree deferred to V1.1 if profiling shows it matters

> **Measured confirmation (2026-05-28):** on a real **3,995-photo** scan the pairwise phase took **1.23 s for 7,978,015 pairs** — confirming the XOR+popcount comparison is effectively free at this scale. The BK-tree would optimize this (already-cheap) phase; profiling shows the actual bottleneck is the **dHash decode** in the step above (176.82 s, 78% of scan time), not the comparison. The BK-tree is therefore the *wrong* optimization to pursue — see §10 and `06_Code_Handoff.md` §11.

---

## 6. Grouping Rules

After Phases 2 and 3, we have a graph of pairwise relationships. Convert to groups:

1. **Union-find** across both Exact and Visual relationships → connected components
2. Each component of size ≥ 2 becomes a **Duplicate Group** shown to the user
3. Components of size 1 (no duplicates) are shown in the slideshow without a review step

**Code:** `UnionFind` in `src/EasyPhotoShow.Core/DuplicateDetection/UnionFind.cs` (path-compression + rank); used by `DuplicateDetector.BuildGroups()`.

**Edge case — bridging:** If photo A is a Visual duplicate of B, and B is a Visual duplicate of C, but A and C are not similar (e.g., a long chain), they still form one group. This matches user mental models ("these are all the same scene") even when the chain stretches. Verified by `UnionFindTests.Union_TransitiveLinks_BridgesGroups`.

---

## 7. "Recommended" Photo Heuristic

Each Duplicate Group has one photo marked as recommended (visually emphasized in the UI). Ranking, in order:

1. **Highest resolution** (width × height)
2. **Largest file size** (proxy for quality at same resolution)
3. **Earliest `CaptureTime`** (proxy for "the original")
4. **Shortest path** (proxy for "in the main folder, not a backup subdir")
5. **Alphabetical path** (final deterministic tiebreaker)

**Code:** `RecommendedPhotoSelector.Pick()` in `src/EasyPhotoShow.Core/DuplicateDetection/`. Verified by `RecommendedPhotoSelectorTests`.

Deterministic — same input always produces same recommendation.

---

## 8. PotentialDuplicates Folder Behavior

When the user clicks "Continue" on the review screen (after editing checkboxes), unchecked photos move to a `PotentialDuplicates/` folder.

**Code:** `PotentialDuplicatesMover.Move()` in `src/EasyPhotoShow.Core/Files/`.

Behavior:
- For each unchecked photo, move it to `<source_folder>/PotentialDuplicates/`
- If `PotentialDuplicates/` already exists from a prior run, append the new files (no overwrites)
- On name collision: suffix with `_1`, `_2`, etc. via `ResolveCollisionFreeName()`
- Folder structure within `PotentialDuplicates/` is **flat** — no nested mirroring (keeps recovery simple)
- Moves are per-file with try/catch; partial failure leaves remaining work resumable and surfaces failed paths in `MoveReport.Failed`

**Cross-drive case:** if a photo's source folder is read-only or on a removable drive, fall back to a `PotentialDuplicates/` folder in the first writable source folder. `IsWritable()` probes by writing and deleting a 0-byte file.

---

## 9. UI Contract

Implemented in `src/EasyPhotoShow.App/Views/DuplicateReviewView.xaml`:

- 20 groups per page (`DuplicateReviewViewModel.GroupsPerPage`)
- Groups stacked vertically, thumbnails horizontally within each group (WrapPanel)
- Thumbnail size: 220×165 px (uses `ThumbnailConverter` with `DecodeWidth=240`)
- Recommended photo: warm-accent border + accent-soft background + "Recommended" pill badge
- Each photo has a "Use this photo" checkbox (default-checked for recommended)
- **No** percentages, scores, distance values, hash values, or algorithm names anywhere in the UI

**Header structure** (per UX/UI spec):
- "Review Similar Photos" (H1)
- "Found N groups of similar photos across M total photos." (Lead)
- "Recommended photos are already selected. Review and adjust if needed."
- "Unused photos move to a PotentialDuplicates folder. Nothing is deleted."
- **"Use Recommended Choices"** button — prominent, near the top, resets checkboxes to recommended state

**Bottom action bar:**
- Previous · Page X of Y · Next (visually quiet)
- "Include All Photos" (secondary — keeps every photo, moves nothing; equivalent to "Use All Photos" path from main screen)
- "Continue" (primary — honors checkboxes, moves unchecked to `PotentialDuplicates/`)

**Empty state:** When zero duplicate groups are found, the scanning view skips the review screen entirely and navigates directly to slideshow creation.

---

## 10. Performance Budget

Target: complete scan + duplicate detection on **1,000 photos in under 30 seconds** on a mid-range Windows laptop (i5-10th gen, SATA SSD).

| Phase | Per-photo cost | 1,000 photos |
|---|---|---|
| 1. Index (EXIF only) | ~2 ms | ~2 s |
| 2. Exact hash (SHA-256, ~50 files avg) | ~20 ms | ~1 s |
| 3. dHash decode + compute | ~15 ms | ~15 s |
| 3. Pairwise comparison | negligible | ~1 s |
| **Total** | | **~19 s** |

Phases 1 and 3 run on background threads (Phase 3 uses `Parallel.For`) with progress reporting. UI remains responsive throughout.

### Measured at scale (3,995-photo real scan, 2026-05-28)

The estimate table above predates the `[TIMING-SCAN]` instrumentation. Real numbers from a 3,995-photo collection on the developer machine:

| Phase | Measured | Count |
|---|---|---|
| 1. Index (walk + EXIF) | 16.12 s | 3,995 photos, 318 unsupported |
| 2. Exact match (SHA-256) | 32.42 s | 1,652 files hashed (size collisions) |
| 3. dHash decode + compute | **176.82 s** | 3,995 photos — **78% of total** |
| 3. Pairwise comparison | 1.23 s | 7,978,015 pairs |
| Group build (union-find) | 0.00 s | 960 groups |
| **Total** | **226.59 s** | |

The per-photo dHash cost (~44 ms wall, under `Parallel.For`) is higher than the original ~15 ms/photo estimate because it's dominated by **Magick.NET full-resolution decode**, not the hash arithmetic. The Phase-2 SHA-256 cost is also non-trivial here (1,652 of 3,995 photos shared a file size and got fully hashed). A ~4-minute scan on ~4,000 photos is acceptable for a one-time operation, so no optimization ships in V1 — but the decode-cost finding is logged for V1.1 (§11 item 1 / `06_Code_Handoff.md` §10).

### 10.1 Scan timing instrumentation (`[TIMING-SCAN]` log block)

`DuplicateDetector.Detect()` returns a `ScanTimings` object (`src/EasyPhotoShow.Core/DuplicateDetection/ScanTimings.cs`) alongside the groups, carrying per-phase `Stopwatch` wall times for Phases 2–5 plus the relevant per-phase count. Phase 1 (Index) is measured by `ScanningViewModel` around the `FolderScanner.Scan` call (it runs before `Detect`) and stitched onto the returned `ScanTimings`. `ScanningViewModel.WriteScanTimingLog` then writes a `[TIMING-SCAN]` block to `%LOCALAPPDATA%\EasyPhotoShow\logs\scan_<timestamp>_<id>.log`:

```
[timing-scan] Index (walk + EXIF):           16.12 s   (3,995 photos, 318 unsupported)
[timing-scan] Exact match (SHA-256):         32.42 s   (1,652 files hashed)
[timing-scan] dHash decode + compute:       176.82 s   (3,995 photos)
[timing-scan] Pairwise comparison:            1.23 s   (7,978,015 pairs)
[timing-scan] Group build (union-find):       0.00 s   (960 groups found)
[timing-scan] TOTAL:                        226.59 s
```

The log write lives in the App layer (not Core) so `DuplicateDetector` stays free of file-I/O side effects — the same separation `RenderJob`'s `[TIMING-S1]` and `ScanningViewModel`'s autoresolve log use. See `06_Code_Handoff.md` §6.15 for the design rationale and how to add a phase.

**Thumbnail caching (future):** A planned on-disk cache at `%LOCALAPPDATA%\EasyPhotoShow\thumbs\<sha8>.jpg` is not yet implemented. Today thumbnails are decoded on each review-screen render via `ThumbnailConverter`. For collections > a few hundred photos this could feel slow on first scroll. Add the cache before launch.

---

## 11. Open Items

1. **dHash threshold tuning** — Calibrated to 8 bits against a 49-photo real-world test collection (2026-05-26). Additional calibration against other collection types (wedding, vacation, memorial) is still worthwhile before launch but not blocking. Same-scene photos that differ in lighting / angle / cropping can have Hamming distances of 20+ and are not catchable via dHash threshold tuning alone — would need a complementary perceptual hash (pHash or learned model) as a V1.1 workstream.

1a. **dHash decode optimization (V1.1, deferred 2026-05-28)** — `[TIMING-SCAN]` showed dHash decode + compute is **78% of scan time** (176.82 s on 3,995 photos), dominated by Magick.NET full-resolution decode for a 9×8 hash. The lever is decoding cheaper: decode at reduced resolution, or read an embedded EXIF thumbnail when present. Deferred because a one-time ~4-minute scan on ~4,000 photos is acceptable. Note this is a **decode-path** change only — do not touch thresholds, the Hamming compare, or `Parallel.For` parallelism. Explicitly **not** the BK-tree optimization (§5), which targets the already-cheap pairwise phase.

2. **HEIC decode performance** — libheif (via Magick.NET) is single-threaded internally. Stage 3 parallelism gives smaller wins on HEIC-heavy collections; may want to cap HEIC concurrency to 2-4 to avoid memory pressure. Measure before launch.

3. **Should screenshots / non-photo images be excluded from grouping?** A folder with 30 screenshots of the same app may all dHash-match and form one giant useless group. Possible heuristic: skip images without EXIF camera tags. Defer to V1.1 unless it becomes a problem.

4. **Cross-folder duplicates** — If the same photo lives in `Trip2024/` and `Backup/Trip2024/`, the group spans folders. Current behavior: each unchecked photo moves into its own folder's `PotentialDuplicates/`. Confirm this matches user expectations during playtesting.

5. **Resumability** — If the app crashes mid-scan on 10,000 photos, do we cache the hashes to disk and resume? V1 says session-based with no project save, so probably no. Revisit if users complain.

6. **On-disk thumbnail cache** — Planned at `%LOCALAPPDATA%\EasyPhotoShow\thumbs\`; not yet implemented.

---

## 12. Implementation Map (file/line reference)

| Responsibility | File |
|---|---|
| Photo record (with optional Sha256, DHash) | `src/EasyPhotoShow.Core/Models/Photo.cs` |
| DuplicateGroup record | `src/EasyPhotoShow.Core/Models/DuplicateGroup.cs` |
| Format classification | `src/EasyPhotoShow.Core/Scanning/SupportedFormats.cs` |
| Folder walk + EXIF index | `src/EasyPhotoShow.Core/Scanning/FolderScanner.cs` |
| Normalized image decode + EXIF rotate | `src/EasyPhotoShow.Core/Imaging/NormalizedBitmapLoader.cs` |
| dHash compute + Hamming distance | `src/EasyPhotoShow.Core/Imaging/DHash.cs` |
| Three-phase detector (returns groups + `ScanTimings`) | `src/EasyPhotoShow.Core/DuplicateDetection/DuplicateDetector.cs` |
| Per-phase scan timings struct | `src/EasyPhotoShow.Core/DuplicateDetection/ScanTimings.cs` |
| Union-find | `src/EasyPhotoShow.Core/DuplicateDetection/UnionFind.cs` |
| Recommended-photo heuristic | `src/EasyPhotoShow.Core/DuplicateDetection/RecommendedPhotoSelector.cs` |
| Exact-group auto-resolution | `src/EasyPhotoShow.Core/DuplicateDetection/ExactGroupAutoResolver.cs` |
| Move-to-PotentialDuplicates | `src/EasyPhotoShow.Core/Files/PotentialDuplicatesMover.cs` |
| Scan screen ViewModel (+ `[TIMING-SCAN]` log write) | `src/EasyPhotoShow.App/ViewModels/ScanningViewModel.cs` |
| Review screen ViewModel | `src/EasyPhotoShow.App/ViewModels/DuplicateReviewViewModel.cs` |
| Review screen XAML | `src/EasyPhotoShow.App/Views/DuplicateReviewView.xaml` |
| Tests | `tests/EasyPhotoShow.Core.Tests/{DHashTests,UnionFindTests,RecommendedPhotoSelectorTests,GroupClassificationTests,ExactGroupAutoResolverTests,OrientationTests}.cs` |
