# EasyPhotoShow — Documentation

This folder is the **canonical home** for EasyPhotoShow V1 documentation. It is designed to serve two purposes:

1. **Loaded into a Claude Project** for ongoing detailed discussion and code review of the app.
2. **Self-sufficient handoff** — if something happens to the original developer's Claude account, a different human developer or AI can pick up these documents and finish the project from them alone.

Every document here is current as of **2026-05-28**. The app is **fully scaffolded, renders MP4s end-to-end, and is 34/34 tests green**. It has been validated on a large real batch (a 3,995-photo scan + render completed successfully across multiple source folders). A handful of pre-launch items remain — see `06_Code_Handoff.md` §10.

---

## What's here

| File | Purpose | When to read |
|---|---|---|
| `01_Product_Specification.md` | Authoritative description of what V1 IS (features, workflow, exclusions, philosophy). | First. Defines the product surface. |
| `02_DuplicateDetection_Design.md` | Three-phase pipeline (Index / SHA-256 / dHash). Grouping rules. Recommended-photo heuristic. PotentialDuplicates folder rules. | Before changing the review screen or duplicate logic. |
| `03_BestMix_Design.md` | Default ordering algorithm. Event clustering + proportional interleave with dHash variety tiebreaker. Determinism guarantees. | Before changing photo ordering. |
| `04_ExportPipeline_Design.md` | 3-stage render: Normalize → chunked Render → concat + Finalize. Encoder ladder (LGPL-aware). Cheap-blur. Progress reporting. Incident log. | Before any rendering or FFmpeg work. The most evolved doc — read it carefully. |
| `05_UX_UI_Specification.md` | Design language, color philosophy, typography, layout, button hierarchy. As-implemented decisions per screen. | Before any UI work. |
| `06_Code_Handoff.md` | Architecture map, file-by-file responsibilities, build/run/test, load-bearing decisions, open items, incident log, "where to look when X happens." | The one-stop entry point for a stranger picking up the codebase. |

---

## Recommended reading order

### If you're a returning Claude session in this project
- `06_Code_Handoff.md` §2-§4 for orientation
- Whichever design doc covers the area you're touching
- The "Load-bearing decisions" section of `06_Code_Handoff.md` (§6) before changing anything risky

### If you're a new developer (human or AI) picking up the project
1. `01_Product_Specification.md` end-to-end (~15 min) — know what we're building
2. `06_Code_Handoff.md` §1-§6 (~20 min) — know how it's organized and what NOT to break
3. `06_Code_Handoff.md` §10-§11 (~10 min) — know what's still open and what bumps we already hit
4. Skim `02`-`05` (~10 min each) — read in full when you're about to touch the corresponding code
5. Run `dotnet build` then `dotnet test` then `dotnet run --project src/EasyPhotoShow.App`. Drop in a folder of photos. Make a slideshow.
6. Pick a TODO from `06_Code_Handoff.md` §10 — ship it.

### If you're loading these into a Claude Project for review
Add all 7 files (this README + the 6 numbered docs). They cross-reference each other heavily and assume sibling availability. The 6 numbered docs total ~30K words; well within Claude's context for a 200K-token model.

---

## Conventions used in these docs

- **File paths** are written relative to the repo root `E:\Dev\EasyPhotoShow\`.
- **Code references** use the form `src/EasyPhotoShow.Core/Rendering/RenderJob.cs` (forward slashes, language-neutral). On Windows these resolve identically with backslashes.
- **`As implemented:`** callouts in the design + UX docs capture the actual code state where it differs from or extends the original design.
- **"Don't"** callouts mark decisions you should NOT undo without understanding why. Always look at the linked incident or the load-bearing-decisions list before reversing.
- All timestamps are absolute dates, never relative ("2026-05-25" not "yesterday").

---

## What's NOT in this folder

- **The code itself.** It lives in `../src/` and `../tests/` from this folder's perspective.
- **External assets:**
  - FFmpeg binaries at `../src/EasyPhotoShow.App/tools/ffmpeg/` (LGPL, ~330 MB, not committed)
  - Music preset MP3s at `../src/EasyPhotoShow.App/Assets/Music/` (royalty-free, not yet acquired)
- **Source icon files** at `../Icons/`. Final-form `.ico` lives at `../src/EasyPhotoShow.App/Assets/easyphotoshow.ico`.
- **Per-session memory** (Claude Code only) at `C:\Users\Admin\.claude\projects\E--Dev-EasyPhotoShow\memory\`. Most of that content has been promoted into these docs. The memory store remains for ongoing AI-session continuity.

---

## Where the project lives

- **Local path:** `E:\Dev\EasyPhotoShow\` on the original developer machine.
- **Git:** Not yet initialized. Recommended pre-V1.0: `git init`, `.gitignore` for `bin/`, `obj/`, `*.user`, `tools/ffmpeg/*.exe`, `Assets/Music/*.mp3`, `Errors/`, then push to a private remote.
- **Owner:** hello@easyphotoshow.com (coordinate before any commercial distribution).

---

## Pre-launch checklist (TL;DR)

From `06_Code_Handoff.md` §10:

- [ ] Acquire royalty-free MP3s for Celebration / Peaceful / Reflective presets
- [ ] Code signing certificate ($200-400/yr)
- [ ] H.264 patent licensing review (MPEG-LA terms)
- [ ] Trial-to-paid licensing/upgrade mechanism (license key + payment flow)
- [ ] Inno Setup installer script
- [ ] Calibrate `DHash.SimilarityThresholdBits` (currently 8) against representative photo sets
- [ ] Calibrate `FilterGraphBuilder.BlurSigma` (currently 8 at 480×270) visually
- [ ] Initialize git repository and push to private remote
- [ ] Decide on thumbnail on-disk cache (perf for large duplicate-review sets)
