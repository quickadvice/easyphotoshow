# EasyPhotoShow V1 — Product Specification

## Status
**Implementation Stage** — fully scaffolded and runnable end-to-end.
Last updated: 2026-05-25.

V1 features described in this spec are implemented behind a WPF UI on .NET 8.
Detailed design and code references live in the companion documents:

- `02_DuplicateDetection_Design.md`
- `03_BestMix_Design.md`
- `04_ExportPipeline_Design.md`
- `05_UX_UI_Specification.md`
- `06_Code_Handoff.md`

Pre-launch items that remain open (licensing, code signing, music tracks, calibration passes) are tracked in §20 and in detail in `06_Code_Handoff.md`.

---

# 1. Product Identity

## What EasyPhotoShow Is

EasyPhotoShow is a fast, local-first Windows slideshow application designed for non-technical users handling large photo collections.

The software focuses on:
- simplicity
- speed
- reliability
- low-stress workflows
- safe duplicate cleanup
- polished slideshow generation

EasyPhotoShow is designed to help users quickly create slideshow videos for meaningful life events.

## What EasyPhotoShow Is NOT

EasyPhotoShow is NOT:
- a professional video editor
- a timeline-based editor
- a cloud collaboration platform
- an AI creative suite
- a cinematic production tool
- a social media editing app

Users wanting advanced editing can export the generated MP4 into another editor. EasyPhotoShow focuses on creating a strong foundational slideshow quickly and reliably.

---

# 2. Core User Types

Primary expected users include parents, grandparents, churches, schools, coaches, memorial organizers, funeral homes, families handling memorial services, graduation organizers, and casual home users.

Many users may be emotionally stressed, rushed, non-technical, using large messy photo collections with duplicate-heavy folders, and on external drives or flash drives.

---

# 3. Emotional Design Philosophy

EasyPhotoShow should feel calm, dependable, respectful, predictable, and approachable.

The software should:
- reduce stress
- avoid technical overwhelm
- avoid destructive actions
- protect original files
- avoid confusing decisions
- maintain a calm tone during errors and rendering

The software should NEVER:
- aggressively upsell
- watermark emotional projects
- use technical jargon unnecessarily
- punish mistakes harshly
- overwhelm users with settings

---

# 4. Product Goals

- extremely simple workflow
- fast startup
- local/offline operation
- reliable handling of 1,000+ photos
- safe duplicate review
- polished slideshow output
- low learning curve
- predictable behavior

The software should feel usable within the first session without tutorials.

In a direct benchmark comparison, EasyPhotoShow rendered a 50-photo 1080p slideshow with xfade transitions and blurred fill backgrounds in 2 minutes 39 seconds on a mid-range Intel iGPU. Clipchamp — Microsoft's built-in cloud-based video editor — took 2 minutes 55 seconds for the same 50 photos at 1080p with no transitions and no blurred fill, and required uploading the photos before export and downloading the result afterward. EasyPhotoShow produces a more polished output faster, on local hardware, with no upload, no download, and no account required.

---

# 5. Supported Platforms

Version 1: **Windows only**.

Reasoning: reduced complexity, improved reliability, simpler support, better performance optimization, faster development focus.

---

# 6. Supported Formats

## Photos
- JPG
- JPEG
- PNG
- HEIC
- HEIF

HEIC/HEIF support ensures iPhone photos work without manual conversion.

Unsupported formats are skipped safely, never crash the application, and are reported after scanning:
> "17 unsupported files were skipped."

## Audio
- MP3 only

The software supports one uploaded audio track that loops automatically during slideshow creation.

---

# 7. Trial Version Rules

## Trial Limits
- maximum 50 photos
- maximum 5-minute slideshow

Trial includes duplicate review, slideshow generation, transitions, music support, full export quality.

Trial does NOT include watermarks, account requirements, time-limited expiration.

If limits are exceeded, the user receives a calm warning and may reduce photos or upgrade.

**Implementation note:** Trial gating is always-on today. No licensing/upgrade mechanism is implemented yet — that is a pre-launch item (see §20).

---

# 8. Core Workflow

## Step 1: Launch Application

Startup feels immediate, lightweight, uncluttered.

The main screen includes:
- the EasyPhotoShow app icon and wordmark
- Add folder button
- selected folder list
- "Review Similar Photos First" option (recommended path)
- "Use All Photos" option (skip photo review)

Users may select one or more folders.

## Step 2: Choose Workflow

### Option A — Review Similar Photos First
Recommended path. Helps remove repeated and very similar photos before slideshow creation.

### Option B — Use All Photos
Fastest path. Skips the duplicate-review screen and uses every supported photo found.

---

# 9. Duplicate Detection System

## Duplicate Matching Criteria

The system evaluates file size, file name, exact hashing (SHA-256 on size-matched files), and perceptual image similarity (dHash with Hamming distance).

Technical implementation: see `02_DuplicateDetection_Design.md`.

## Duplicate Review Philosophy

The software **NEVER** deletes files. Unused duplicate photos are moved into a `PotentialDuplicates/` folder and excluded from slideshow generation. Movement only occurs **AFTER** user approval.

## PotentialDuplicates Folder Rules

Duplicate photos move into a `PotentialDuplicates/` folder inside the original source path:

```
C:\photos\AuntBea\PotentialDuplicates\
E:\photos\PotentialDuplicates\
```

This preserves user trust, recovery simplicity, and predictable behavior.

## Duplicate Review Screen

Layout (as implemented):
- 20 duplicate groups per page
- duplicate groups displayed vertically
- thumbnails displayed side-by-side, 220×165 px
- recommended photo visually emphasized with warm-accent border and "Recommended" pill badge
- "Use this photo" checkbox per thumbnail
- prominent "Use Recommended Choices" button near the top (one-click confidence builder)

No confidence percentages, technical similarity scores, or algorithm explanations.

Bottom action bar:
- Previous · Page X of Y · Next (visually quiet)
- "Include All Photos" (secondary; keeps every photo in the slideshow, moves nothing)
- "Continue" (primary; honors checkboxes — moves unchecked to `PotentialDuplicates/`)

---

# 10. Slideshow Creation Screen

## Slideshow Summary
Display:
- total photo count
- seconds per photo (editable, 1.0–20.0)
- estimated runtime (visually emphasized in a warm-accent card)

Runtime updates automatically.

## Photo Ordering

### Default: Best Mix
Should:
- spread out visually similar images
- avoid same-event clustering
- avoid repetitive sequences
- create natural slideshow variety
- be deterministic (same input always produces same order)

Technical implementation: see `03_BestMix_Design.md`.

### Optional: Keep Folder Order
Uses source file ordering.

---

# 11. Transition Options

V1 transition options:
- Fade
- Smooth
- Push
- Dissolve
- Zoom
- Random

---

# 12. Music Options

V1 music options:
- None
- Celebration (preset — MP3 not yet shipped)
- Peaceful (preset — MP3 not yet shipped)
- Reflective (preset — MP3 not yet shipped)
- Upload MP3 (custom file)

Uploaded music accepts one MP3 file, loops automatically, and displays filename and duration in the UI.

**Implementation note:** Preset MP3s are loaded from `Assets/Music/{name}.mp3` at runtime. If the file is missing the preset silently produces no audio. Acquiring royalty-free tracks is a pre-launch item.

---

# 13. Export Rules

## Export Format
Version 1 exports MP4 only.

Technical implementation: see `04_ExportPipeline_Design.md`.

## Export Resolution
1080p only. No advanced export menus.

## Photo Framing

The slideshow output is 16:9 (1920×1080). Source photos in other aspect ratios (portrait, 4:3, panoramas) are displayed using a blurred-background fill:

- the original photo is shown intact, centered, scaled to fit
- a blurred, enlarged copy of the same photo fills the remaining frame
- no cropping, no black bars

EXIF orientation is honored for every photo, including iPhone HEIC files.

## Save Location Selection

Before rendering begins:
- user selects slideshow name
- user selects save location (defaults to the first source folder)

Default filename rules:
- One source folder: `[Folder Name] Slideshow.mp4`
- Multiple source folders: `My Slideshow.mp4`
- Auto-incremented to `Name (2).mp4`, `Name (3).mp4` on collision (no prompt during default naming)
- Overwrite prompt only if the user manually selects an existing name

System validates available disk space, write permissions, and drive availability. Conservative free-space estimate shown: "Recommended free space: 5 GB" (or larger for long slideshows).

---

# 14. Rendering Behavior

## Rendering Philosophy

EasyPhotoShow prioritizes reliably fast rendering and stability on large projects. The system internally adapts to project size, estimated duration, and available system resources. No user-facing speed/stability modes.

## Rendering Screen

Display:
- Title: "Creating your slideshow"
- Current stage message (Lead text), one of:
  - "Preparing your photos..."
  - "Creating video and adding transitions..."
  - "Adding music..."
  - "Saving your slideshow..."
  - "Almost done..."
- Overall progress bar (warm beige track, brand-blue fill)
- Left: phase-specific photo progress (e.g., "Preparing photo 28 of 38", "Building frame 28 of 38", "All photos prepared", "All frames built", "Slideshow ready")
- Right: "Overall progress: X%" (bold)
- Below: ETA (e.g., "Estimating time...", "About 1 minute remaining", "About 3 minutes remaining", "Almost done")
- Cancel button (secondary, calm)

Phase-specific wording ensures the user never sees the same "Photo X of Y" label twice across phases (which previously created the appearance of going through photos twice).

## Rendering Controls

Allowed: minimize window, cancel slideshow.

## Closing During Render

If the user attempts to close the application:

> "Your slideshow is still being created. Closing EasyPhotoShow now will stop the slideshow before it is complete."

Buttons: Keep Building · Stop and Exit.

---

# 15. Playback Behavior

After successful rendering:

> "Your slideshow is complete. Would you like to view it now?"

Buttons: View Slideshow · Open Folder · Done. "Saved to:" label above the file path; path is small and visually quiet.

## Built-In Playback

Playback occurs INSIDE EasyPhotoShow.

Supported controls:
- Play/Pause (warm accent — primary action)
- Replay
- Volume slider (custom slim style)
- Position scrubber (custom slim style with accent thumb)
- Current time / total time
- Fullscreen
- Close

Playback opens in windowed mode first; user can switch to fullscreen manually. The control bar uses a warm charcoal (`#1A1714`) background, not pure black, to feel softer.

---

# 16. Session Philosophy

Version 1 is session-based. The application does NOT save projects, maintain project history, or display a recent-slideshow dashboard.

---

# 17. Performance Expectations

The software should:
- start quickly
- remain responsive during scanning
- progressively load thumbnails
- avoid freezing UI
- support large photo collections
- support external drives reliably

---

# 18. Error Handling Philosophy

Errors should use calm wording, avoid technical jargon, explain what happened clearly, and preserve user trust.

The software should NEVER:
- silently fail
- destroy originals
- freeze without feedback
- expose raw FFmpeg logs to users

Failure messages are mapped to plain language with a debug log saved to `%LOCALAPPDATA%\EasyPhotoShow\logs\` for support.

---

# 19. Installer Philosophy

Installation should be lightweight, fast, low-friction. Avoid mandatory accounts, launcher ecosystems, forced cloud sync, complicated activation flows.

Licensing philosophy: one-time purchase, simple ownership, no subscriptions.

---

# 20. Technical Direction

## Confirmed Stack

- C# on .NET 8
- WPF for the desktop UI
- FFmpeg (LGPL build) as the rendering backend, bundled with the installer
- FFMpegCore (NuGet) as the FFmpeg wrapper
- MetadataExtractor (NuGet) for EXIF reads
- Magick.NET-Q8-AnyCPU (libheif-backed) for decode + HEIC/HEIF support
- CommunityToolkit.Mvvm for view-model boilerplate
- Inno Setup for the Windows installer (not yet implemented)

Rationale and per-component design details: see the companion design documents.

## Pre-Launch Items Still Open

- **Code signing certificate** — required to avoid SmartScreen warnings on first install
- **H.264 patent licensing review** — for commercial distribution
- **Trial-to-paid licensing/upgrade mechanism** — license key entry, payment flow
- **Music preset MP3 files** — royalty-free Celebration / Peaceful / Reflective tracks needed at `Assets/Music/`
- **Calibration passes** — dHash similarity threshold (currently 8 bits) and blurred-background `gblur` sigma (currently 8 at 480×270) tuned against representative photo sets
- **Installer (Inno Setup)** — Windows installer script + uninstaller + Start Menu/Desktop shortcuts
- **FFmpeg binary distribution** — `tools/ffmpeg/ffmpeg.exe` and `ffprobe.exe` must be bundled into the installer payload

---

# 21. Explicit V1 Exclusions

## Editing Features Excluded
- timeline editor
- manual sequencing editor
- text overlays
- captions
- animated text
- stickers
- crop editor
- filters
- color correction

## Audio Features Excluded
- multiple songs
- music trimming
- narration tracks
- fade editing
- beat syncing

## Export Features Excluded
- codec selection
- bitrate controls
- FPS settings
- resolution selection
- advanced rendering menus

## AI Features Excluded
- AI narration
- AI captions
- AI image enhancement
- AI slideshow themes
- AI photo generation

## Cloud Features Excluded
- cloud sync
- collaborative editing
- browser-based editing
- online storage

---

# 22. Core Product Philosophy

Whenever uncertainty exists, EasyPhotoShow should prioritize:
- simplicity over complexity
- speed over flexibility
- reliability over flashy features
- confidence over customization
- calmness over cleverness

The software should make users feel:
> "I can do this."
