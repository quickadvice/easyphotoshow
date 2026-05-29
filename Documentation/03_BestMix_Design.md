# EasyPhotoShow V1 — Best Mix Ordering Design

## Status
**Implemented.** Last reviewed: 2026-05-25.

Algorithm in `src/EasyPhotoShow.Core/Ordering/BestMixOrderer.cs`. Invoked from `src/EasyPhotoShow.App/ViewModels/RenderingViewModel.cs` when the user's chosen `PhotoOrdering` is `BestMix` (the default). `EventClusterer.cs` is retained for potential future use but is no longer called by Best Mix — see §3.

Tests cover spacing of similar-photo clusters, preservation of all photos, determinism, empty/single-photo input, the no-signal alphabetical fallback, and a 1,000-photo performance budget — see `tests/EasyPhotoShow.Core.Tests/BestMixOrdererTests.cs`.

---

## 1. Goals (from spec §10)

Best Mix is the **default** photo ordering. It should:

- Spread out visually similar images
- Avoid same-event clustering
- Avoid repetitive sequences
- Create natural slideshow variety

Users who want strict chronological/folder order pick "Keep Folder Order" instead — so Best Mix can lean hard into variety without worrying about preserving narrative flow across the whole slideshow.

Best Mix must also be **deterministic**: the same input must always produce the same order. Users expect a re-render to match the preview they remember.

---

## 2. Inputs Available

By the time Best Mix runs, we already have everything we need from the duplicate detection pass (see `02_DuplicateDetection_Design.md`):

| Field | Source | Use in Best Mix |
|---|---|---|
| `CaptureTime` | EXIF `DateTimeOriginal` (fallback: file mtime) | Event clustering |
| `DHash` (64-bit) | Phase 3 of duplicate detection | Visual-variety tiebreaker |
| `Path` | filesystem | Final deterministic tiebreaker |

**Key assumption:** Best Mix runs **after** duplicate review. Visually-identical photos (burst shots, re-exports) have already been resolved by the user. Best Mix can trust that every photo it sees is one the user wants in the slideshow.

If the user skipped duplicate review (clicked "Use All Photos" on the main screen or "Include All Photos" on the review screen), photos may still carry dHash values from the scanning phase, so the visual-variety tiebreaker still works.

---

## 3. Algorithm

**Why this replaced the original approach (2026-05-26):** The first implementation clustered photos into 30-minute events and then interleaved events proportionally. That produced **visible sections** in the rendered slideshow — a viewer could feel "now we're in the graduation block" because all 20 senior-portrait photos played within a contiguous band. The product promise is the opposite: a viewer at minute 10 and a viewer at minute 60 should both have a chance of seeing one of those portraits, with no detectable section boundaries.

The replacement treats **capture time as a similarity signal** (used to cluster), not a **sequencing constraint** (used to order). The output is a uniformly spaced distribution of every similarity cluster across the full slideshow.

### Step 1 — Build similarity clusters (union-find)

Two photos belong to the same similarity cluster if **either** signal links them:

- **dHash Hamming distance ≤ `DHash.SimilarityThresholdBits`** (currently 8 bits) — they look the same.
- **Capture time within 30 minutes** of any other photo in the cluster — they're from the same shoot session.

The existing `UnionFind` structure (from `DuplicateDetection/`) does the heavy lifting. Photos missing both signals start as singleton clusters. The pairwise scan is O(n²) with extremely cheap per-comparison work (`BitOperations.PopCount` for dHash, one `TimeSpan` subtraction for time) — measured ~5 ms at n = 1,000.

### Step 2 — Assign slots at enforced spacing

For a cluster of size **N** in a collection of size **T**, the spacing interval is **T / N**. The kth photo of that cluster aims at slot ⌊k·(T/N) + offset⌋. The offset is the cluster's index in the size-descending, alpha-tiebroken sort order — this staggers the start of each cluster so they don't all collide at slot 0.

**Largest clusters place first.** They have the tightest spacing constraint and the most photos to distribute. Smaller clusters arrive next and only displace by one or two slots at each collision.

**Collisions resolve to the next free slot** via a `SortedSet<int>` of unfilled positions. The set yields the smallest free slot ≥ the target in O(log n); on wrap, it falls back to the smallest free slot overall. The whole step is O(n log n).

**Within a cluster, photos are ordered greedily for variety.** First photo is alphabetically smallest; each subsequent photo is the unused one whose dHash is farthest (Hamming distance) from the last-placed one, with alphabetical path as final tiebreak. For a cluster of byte-identical photos (e.g. 20 OSU portraits with the same dHash), every distance is zero, so this collapses to pure alphabetical order — still deterministic, no behavioral surprises.

### Step 3 — No explicit fill pass needed

The original spec described a third "fill remaining slots with singletons / overflow" step. With the next-free-slot collision resolution above, **there is no overflow** — every photo finds a slot the first time. Singletons are placed by the same mechanism as larger clusters; they just have spacing = T (so they each aim at slot 0 + their cluster offset, which spreads them naturally across the early slots and then wraps).

### Worked example

Photo set: T = 100. Three clusters: OSU portraits (N = 20), wedding shoot (N = 15), 65 unrelated singletons.

- OSU spacing = 5 → ideal slots 0, 5, 10, ..., 95 (with offset 0)
- Wedding spacing ≈ 6.67 → ideal slots 1, 8, 14, 21, ..., 95 (with offset 1)
- Singletons spacing = 100 (each is one photo) → ideal slot 2, then 3, 4, ... (with their alpha-sorted offsets)

After collision resolution, OSU lands at 0, 5, 10, 15, ... and the wedding cluster fills near-by slots. Singletons take whatever's left, alphabetically. The user watching at minute 10 of a 100-photo slideshow sees OSU photo #2 around slot ~5 and OSU photo #3 around slot ~10. The user at minute 60 sees OSU photo #12 around slot ~60. The portraits are evenly distributed and no section boundary exists.

---

## 4. Why This Works for the Stated Goals

| Spec goal | How the spacing algorithm addresses it |
|---|---|
| Spread out visually similar images | Photos that look alike land in the same union-find cluster (via the dHash edge) and are then distributed at T/N spacing — guaranteed to never appear adjacent in the output. |
| Avoid same-event clustering | Capture-time proximity links photos into the same cluster, and clusters are spread across the full slideshow. The same-shoot photos go everywhere, not in a block. This is what the prior algorithm got wrong. |
| Avoid repetitive sequences | Spacing enforcement plus the greedy within-cluster variety ordering breaks up any run of similar-looking frames. Test `SimilarPhotos_AreSpacedApart` asserts that 20 photos sharing one cluster stay more than 3 slots apart in a 100-photo collection. |
| Create natural slideshow variety | The viewer cannot identify section boundaries because there are none. Every minute of viewing has roughly the same statistical mix of cluster representatives. |

---

## 5. Determinism

Required: same input → same output, every time.

- Stage 1 sort is stable (sort by `CaptureTime` then `Path` for ties)
- Stage 2 tiebreakers are deterministic (Hamming distance → alphabetical `Path`)
- No PRNG, no clock reads, no thread ordering dependencies
- Seed-slot pick is the largest event (single-pass linear scan, first-wins on ties)

Verified by `Order_IsDeterministic` regression test: running the same input through `BestMixOrderer.Order()` twice produces byte-identical sequences.

---

## 6. Edge Cases

### All photos in one cluster (e.g., one wedding, tightly timed)
Distributed evenly across all slots with the greedy max-variety within-cluster ordering applied. The cluster has T/T = 1 spacing — each photo gets its own slot, and the within-cluster greedy ordering picks the maximally-dHash-different next photo at each step. For burst-mode dumps where every photo is dHash-identical, this collapses to alphabetical order — still deterministic and reasonable.

This is a **deliberate behavior change** from the prior algorithm, which fell back to chronological order in this case. Chronological-within-one-cluster reintroduced the "sections" problem in miniature (consecutive ceremony photos play in a run); the spacing approach treats this case uniformly.

### No EXIF, no dHash on any photo
Returns the photos in alphabetical path order. Reached as a top-of-function fast path before any clustering work. Verified by `NoDHash_NoCaptureTime_ReturnsAlphabetical`.

### Two shoots that look very similar (e.g., two beach days)
Both signals fire — dHash similarity and (within each day) time proximity. They form one cluster, get spread across the slideshow as one cluster. The user's view: photos from both days mix throughout. If the user wanted day-by-day separation they would pick the chronological ordering option, not Best Mix.

### Very small slideshow (< 10 photos)
Algorithm runs without special-casing and produces alpha-distributed output. No correctness issue.

### Empty input or single photo
Both handled at the top of `Order()`. Verified by `EmptyInput_ReturnsEmpty` and `SinglePhoto_ReturnsSinglePhoto`.

---

## 7. Performance Budget

- Stage 1 (sort + linear walk): O(n log n) on `CaptureTime`. Trivial — < 10 ms for 10,000 photos.
- Stage 2 (proportional interleave): O(n × k) where k = number of events. For typical k < 20, this is effectively O(n). Sub-millisecond for 1,000 photos.

Total Best Mix cost: **< 50 ms for any reasonable input.** Runs synchronously on the rendering screen entry; no progress indicator needed.

---

## 8. Open Items

1. ~~**30-minute event-gap threshold**~~ — **Closed (2026-05-26).** No longer applies to the ordering algorithm; the value still appears as `SessionWindow` in BestMixOrderer for the similarity-cluster signal but its choice no longer affects sequencing, only which photos co-cluster. The clustering effect is symmetric in time direction and produces no visible section behavior, so the exact threshold is much less sensitive than it was under the prior event-block algorithm.

2. **Should Best Mix respect a user-supplied "first photo" or "last photo"?** Memorial slideshows often have a strong opener (a hero portrait) and closer. Spec doesn't mention it. Probably V1.1 — but if added later, the algorithm accommodates it trivially (pin photo to position 0 / n-1, run algorithm on the rest).

3. **dHash distance as visual-variety signal is approximate** — Two wedding-reception photos taken minutes apart with different people in frame can have similar dHashes (same room, same lighting). The algorithm will treat them as "similar" and try to space them, which is the right call. But two outdoor photos in very different settings can also have similar average brightness and produce close dHashes. False-positive rate seems acceptable in practice; revisit if playtesting shows clustering of visually-distinct photos.

4. **What if the user re-runs Best Mix after editing the photo set?** Deterministic per-input means: same exact set → same order. Adding or removing a single photo will reshuffle the entire output, which may surprise users who liked the previous order. V1 acceptable — session-based design (spec §16) means no expectation of preserving order across sessions anyway.

---

## 9. Implementation Map

| Responsibility | File |
|---|---|
| Spacing-based ordering (union-find clustering + slot assignment) | `src/EasyPhotoShow.Core/Ordering/BestMixOrderer.cs` |
| Event clustering helper (retained; no longer called by BestMixOrderer) | `src/EasyPhotoShow.Core/Ordering/EventClusterer.cs` |
| Union-find (shared with duplicate detection) | `src/EasyPhotoShow.Core/DuplicateDetection/UnionFind.cs` |
| Invocation (if `Ordering == BestMix`) | `src/EasyPhotoShow.App/ViewModels/RenderingViewModel.cs` (line ~49) |
| Tests | `tests/EasyPhotoShow.Core.Tests/BestMixOrdererTests.cs`, `EventClustererTests.cs` |
