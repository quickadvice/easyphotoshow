# EasyPhotoShow V1 — UX/UI Design Specification

## Status
**Implemented.** Last reviewed: 2026-05-28.

The principles in this document are normative and should guide future UI work. Implementation notes throughout (in "**As implemented:**" callouts) capture the as-built choices — colors, controls, layout decisions — so the next person editing the UI doesn't accidentally re-litigate settled decisions.

---

## Purpose

This document defines the visual, emotional, and interaction design language for EasyPhotoShow Version 1.

The application should feel:
- calm
- approachable
- dependable
- emotionally safe
- easy for non-technical users

**IMPORTANT:** Do NOT redesign workflow behavior while implementing visual changes. The goal of this document is visual improvement, emotional polish, spacing refinement, hierarchy refinement, consumer-friendly presentation — NOT changing application workflow, navigation structure, or introducing advanced UI complexity.

---

# Core Emotional Goal

EasyPhotoShow should feel like:
> a calm and dependable photo utility made for real people.

NOT:
- enterprise admin software
- a developer utility
- a professional media editor
- a technical rendering tool

The UI should feel warm, calm, spacious, reassuring, simple, emotionally approachable. Especially important for memorial slideshows, funeral services, graduations, family events, emotionally stressed users.

---

# Visual Philosophy

## Desired Feel
spacious · breathable · uncluttered · confident · visually soft · emotionally safe · polished · intentional · modern · lightweight

## Avoid
flashy · futuristic · gamer-oriented · corporate-enterprise styled · cramped layouts · tiny buttons · tiny text · overly dense information blocks · hard sharp borders everywhere · excessive grayscale · giant toolbars · ribbon interfaces · nested menus · heavy visual chrome · excessive gradients · neon colors · high visual aggression

The UI should NEVER feel intimidating or technical.

---

# Color Philosophy

## Base Colors

Primary background: warm off-white / soft cream / light warm neutral.

Avoid: cold gray backgrounds, harsh white, enterprise blue-gray styling.

**As implemented** (in `src/EasyPhotoShow.App/Styles/Theme.xaml`):
- `BackgroundColor` `#F7F5F1` — warm off-white
- `SurfaceColor` `#FFFFFF` — pure white for cards
- `SurfaceMutedColor` `#FBF9F4` — soft cream for inner cards (e.g., duplicate-group photo items)
- `BorderColor` `#E5E1D7` / `BorderSoftColor` `#EFEBE1` — gentle warm borders
- `TextPrimaryColor` `#2A2722` — soft dark brown (not pure black)
- `TextSecondaryColor` `#6F6B61` — warm gray for secondary text

## Accent Colors

Primary accent: warm gold / soft copper / muted amber / warm tan. Should feel trustworthy, warm, calm, confident.

Avoid: neon colors, bright corporate blue, aggressive red, saturated gamer colors.

**As implemented:**
- `AccentColor` `#8B6F3F` — warm gold (primary buttons, accent borders)
- `AccentHoverColor` `#75592D` — slightly darker on hover
- `AccentSoftColor` `#EDE2C9` — warm cream for highlighted areas (Recommended photo background, runtime card, trial-warning callout)

## Brand Blue (icon + progress)

The app icon is a warm-photo-memories design with a **blue** rounded-square background. The icon's blue is the **brand color**. To create high-contrast visibility for the most critical "the app is working" indicator (the rendering progress bar), we use the brand blue rather than the warm gold:

- `ProgressTrackColor` `#EADFC8` — warm beige (matches the rest of the app)
- `ProgressFillColor` `#1E88E5` — brand blue fill (matches the icon)

This is the only place the brand blue appears in the in-app UI. Everywhere else uses the warm gold accent. The split is intentional: warm gold for *user actions*, brand blue for *system status*.

---

# Typography Rules

Typography should feel readable, relaxed, approachable, clean. Use generous line spacing and visual breathing room.

Avoid tiny utility fonts, dense text walls, overly technical formatting.

**As implemented:** A scale defined in `Theme.xaml`, all with explicit `LineHeight` to enforce breathing room.

| Style key | Size · weight · line height | Used for |
|---|---|---|
| `Display` | 40pt Light · LH 48 | Main screen wordmark |
| `H1` | 30pt SemiBold · LH 38 | Screen titles |
| `H2` | 20pt SemiBold · LH 26 | Card headings |
| `Lead` | 17pt · LH 26 | Subheadings under H1, stage messages |
| `Body` | 15pt · LH 22 | General body text |
| `Subtle` | 15pt secondary color | Less emphasized body |
| `Caption` | 12pt muted | Field labels (ALL CAPS), captions |

Font family: Segoe UI for both heading and body.

---

# Layout Philosophy

All screens should use generous padding, consistent margins, strong alignment, clean grouping.

Spacing should feel intentional, calm, breathable. Avoid squeezing content together, maximizing density, edge-to-edge clutter.

**As implemented:**
- Cards have `CornerRadius=12`, `BorderBrush=BorderSoftBrush`, default `Padding=32` (reduced to `28,22` on the Slideshow Settings screen where many cards stack)
- A `SoftCard` variant uses `SurfaceMutedBrush` background for inner emphasis
- Outer screen margins typically 40-48 px

---

# Button Hierarchy Rules

## Primary Buttons
Examples: Continue, Create Slideshow, View Slideshow, Use Recommended Choices.

Should visually dominate naturally, use accent color, have generous padding, feel inviting and confident. Rounded corners, larger click area, stronger visual weight.

**As implemented (`PrimaryButton` style):** `AccentBrush` background, white text, 28×14 padding, 8px corner radius, 48px min height, SemiBold 15pt.

## Secondary Buttons
Examples: Open Folder, Browse, Include All Photos, Cancel, Done.

Should appear quieter, use neutral styling, remain obvious but not dominant.

**As implemented (`SecondaryButton` style):** Transparent background with 1px `BorderBrush`, primary text color, 22×12 padding, 8px corner radius, 48px min height. Hover fills with `HoverBrush`.

## Quiet Buttons
Used for: Previous/Next pagination, Back, low-emphasis controls.

**As implemented (`QuietButton` style):** Transparent, no border, secondary text color (gray-tan), 16×8 padding, 36px min height. Hover fills with `HoverBrush`. Recedes visually so primary actions draw the eye first.

## Warning / Destructive Buttons
Examples: Stop and Exit (close-during-render dialog).

Should visually separate from primary flow, avoid accidental clicks, use softer warning treatment. **Avoid giant alarming red buttons.**

**As implemented:** The close-during-render warning is a standard `MessageBox.Show` (system dialog) with OK/Cancel — Windows handles the safety affordance. No custom destructive button styling in V1.

---

# ComboBox Styling

Default Windows ComboBox chrome looks too enterprise-admin for this app.

**As implemented:** Full custom `ControlTemplate` in `Theme.xaml`:
- Warm white surface with 1px warm border, 6px radius
- Custom triangular chevron icon in secondary text color
- Hover/focus state with accent border
- Dropdown popup with subtle shadow, accent-soft background for selected item
- All items rendered through `EnumDisplayConverter` so PascalCase enum values display as `"Best Mix"`, `"Keep Folder Order"` (not `"BestMix"`, `"KeepFolderOrder"`)

---

# Progress Bar

Spec wording: visible, rounded, clearly distinct fill.

**As implemented:** Direct two-Border construction (not WPF's `ProgressBar` — that proved unreliable, see Incident 4 in `04_ExportPipeline_Design.md`):

```xml
<Border x:Name="ProgressTrack" Height="12" Background="ProgressTrackBrush" CornerRadius="6">
    <Border HorizontalAlignment="Left" Background="ProgressFillBrush" CornerRadius="6">
        <Border.Width>
            <MultiBinding Converter="{StaticResource FractionToWidth}">
                <Binding Path="ProgressFraction" />
                <Binding Path="ActualWidth" ElementName="ProgressTrack" />
            </MultiBinding>
        </Border.Width>
    </Border>
</Border>
```

- Track: `#EADFC8` (warm beige)
- Fill: `#1E88E5` (brand blue — see Color Philosophy)
- 12 px tall, fully rounded corners
- `FractionToWidthConverter` (IMultiValueConverter) clamps the fraction 0..1 and multiplies by track ActualWidth; NaN-safe
- ViewModel holds 100% for ~450 ms before navigating to Completion so the user sees the bar fill complete

---

# Scanning Screen

Shown while EasyPhotoShow walks the selected folders and — if the user chose "Review Similar Photos First" — runs duplicate detection. It can run for minutes on large collections (a 3,995-photo scan takes ~4 minutes; see `02_DuplicateDetection_Design.md` §10), so its whole job is to make a long wait feel **calm, alive, and safe** — never broken.

## Emotional Goal
Reassuring and unhurried. The user should feel the app is steadily working, sense roughly where it is, and trust that **nothing is being deleted**. Avoid anything that reads as stalled, broken, or technical.

## Layout (top to bottom)
Centered `Card`, same calm framing as the other screens:
- **H1:** "One moment..."
- **Lead (centered):** the current stage label (wording below)
- **Subtle (centered):** secondary status — the live count/position (e.g. "Found 312 photos so far", "Checking photo 2,310 of 3,995")
- **Progress bar** (12 px, warm beige track + brand blue fill — the same component as the Rendering screen)
- **Caption (centered):** "{N} unsupported files were skipped." (only when N > 0; singular "1 unsupported file was skipped.")
- **Exact-copy count** (only after the first exact duplicate is found): a thin divider above an accent-tinted, SemiBold "{N} exact duplicate files handled so far"
- **Completion message** (only during the ~1.5 s closing beat): see below

## Stage wording (plain English — no jargon)
The Lead text walks through these labels as the scan progresses. **No "hash", "SHA", "dHash", "perceptual", "EXIF", or any algorithm name appears anywhere on this screen.** The strings live in `DuplicateDetector.cs` (so the wording sits next to the code that reports it) and flow straight through `ScanningViewModel`:

- Folder walk → "Reading photo details..."
- Exact-match setup → "Checking file names and sizes..."
- Exact-match running → "Checking image resolution..."
- Perceptual pass → "Comparing similar-looking photos..."
- Grouping → "Grouping possible duplicates..."

> **Sync warning:** the determinate progress bar is keyed off the exact string `"Comparing similar-looking photos"`. If you rename it in `DuplicateDetector`, rename the comparison in `ScanningViewModel` too, or the bar silently stays indeterminate through the longest phase. (Inline-commented in both files.)

## Progress bar behavior
- **Determinate** during the perceptual pass ("Comparing similar-looking photos"), where the total photo count is known and the work is long enough to show meaningful movement.
- **Indeterminate** during the folder walk and exact-match phases (fast and/or unknown total).

## Exact-copy running count — text-only, no thumbnails
As exact-duplicate groups are found, the screen surfaces a single running count — **"{N} exact duplicate files handled so far"** — and nothing else. Exact copies are auto-resolved (non-recommended copies moved to `PotentialDuplicates/`) without asking the user, so there is no decision to make here; the count is pure reassurance that work is happening and nothing is lost.

**No thumbnails on this screen (decision 2026-05-28).** An earlier version streamed side-by-side "preview cards" of each exact pair into a rolling window. On a live 3,995-photo scan those thumbnails never finished decoding before the screen advanced and displayed as **blank grey boxes for the entire scan** — which reads as *broken*, not *loading*. Blank boxes are worse than no boxes. The cards (and their "Exact copy — set aside safely" / "These exact copies have been safely set aside — nothing was deleted." labels) were removed; the text count does the reassurance work without depending on image decode.

**Avoid** re-adding thumbnails, skeleton placeholders, or spinners here. Thumbnails belong on the **Duplicate Review Screen**, where they are load-bearing for an actual user decision. Full context in `06_Code_Handoff.md` §11.

## Completion beat (~1.5 s)
When the scan finishes, the working UI is cleared and a single message holds for ~1.5 s before navigating, chosen by what actually happened (exact wording — do not paraphrase):
- Exact copies handled, similar photos to review → "All done — {N} exact duplicates handled. Now showing your similar photos..."
- Exact copies handled, nothing to review → "All done — {N} exact duplicates handled."
- Nothing auto-handled, similar photos to review → "Checking complete. Preparing your review..."
- Nothing found → "All done — no duplicates found."

(Singular/plural: "duplicate"/"duplicates" agrees with {N}.) If similar (visual) groups exist, the app then navigates to the **Duplicate Review Screen**; otherwise straight to the **Slideshow Settings Screen**.

---

# Duplicate Group Card Design

Duplicate review groups are one of the most important screens in the application. Cards should feel easy to scan, approachable, visually calm, spacious.

Should use soft rounded corners, subtle borders, gentle contrast, generous internal padding. Avoid harsh outlines, compressed spacing, dense layouts.

**As implemented:**
- Each group is a `Card`-styled `Border` (12px radius, soft border, surface background, 20×16 padding)
- Photos arranged in a `WrapPanel`
- Each photo is a 232 px wide inner card with 10 px padding, 10 px radius, transparent border by default
- Recommended photo: accent border (2 px, `AccentBrush`) + `AccentSoftBrush` background + "Recommended" pill badge

---

# Thumbnail Rules

Thumbnails should be large enough for confidence, visually easy to compare, evenly aligned. Avoid tiny previews, crowded comparisons.

**As implemented:** 212×159 px display (with `Thumb240` converter decoding to 240px width for crispness). Uses `ThumbnailConverter` which fast-paths JPG/PNG via WPF `BitmapImage` (with EXIF rotation applied manually since WPF doesn't auto-rotate display images), and falls back to `Magick.NET` for HEIC/HEIF.

---

# Recommended Photo Styling

Recommended images should stand out immediately, feel confidently selected, feel trustworthy. Soft warm highlight, subtle accent border, "Recommended" badge or pill, slightly stronger visual prominence.

**Avoid** giant warning colors, flashy emphasis, aggressive glow effects.

**As implemented:**
- 2 px solid `AccentBrush` border (warm gold)
- `AccentSoftBrush` background tint
- "Recommended" pill: filled `AccentBrush` rounded badge with white SemiBold 11 pt text, top-left of the card

---

# Duplicate Review Screen

## Emotional Goal
Should feel **reassuring and easy**. Users should feel safe, in control, not overwhelmed.

## Required Header Structure (exact wording — do not paraphrase)
1. **H1:** "Review Similar Photos"
2. **Lead:** "Found {N} groups of similar photos across {M} total photos."
3. **Subtle:** "Recommended photos are already selected. Review and adjust if needed."
4. **Subtle:** "Unused photos move to a PotentialDuplicates folder. Nothing is deleted."
5. **Primary button:** "Use Recommended Choices" — prominent, top-right of the header, one of the most important confidence-building features.

## Duplicate Group Layout
Each group: generous spacing, visually separate, easy side-by-side comparison. Avoid tightly packed rows, crowded checkboxes, spreadsheet-like appearance.

## Checkbox Rules
Clean and modern, comfortable spacing, visually obvious. Label: **"Use this photo"** — not technical, not negative.

## Bottom Action Bar
- Left: Previous · Page X of Y · Next (`QuietButton` style — visually quiet)
- Right: "Include All Photos" (secondary) · "Continue" (primary)

The Continue button naturally draws the eye first.

**"Include All Photos" semantics (resolved 2026-05-25):** This button keeps every photo in the slideshow and **moves nothing** to `PotentialDuplicates/`. Originally labeled "Skip Review" then "Continue Without Review" — both were confusing because users on the review screen had already opted in to duplicate handling. The current label is honest about what happens: every photo is included. Equivalent to the "Use All Photos" path from the main screen.

---

# Slideshow Settings Screen

Three stacked cards: Summary · Style · Music · Save.

## Summary card
Three columns: PHOTOS count · SECONDS PER PHOTO (editable) · ESTIMATED RUNTIME.

**Estimated runtime is visually emphasized** — wrapped in a warm-accent-tinted soft card with a large 30pt SemiBold number. Users care strongly about how long the finished slideshow will be.

## Style card
Two columns: PHOTO ORDER dropdown · TRANSITION dropdown.

## Music card
PHOTO ORDER-style dropdown for presets (None, Celebration, Peaceful, Reflective) + an "OR / Upload MP3" secondary button. After upload, displays `filename · m:ss` below the music section (duration via ffprobe through `MusicMetadataProbe`).

## Save card
Two columns: NAME textbox (default: `[Folder] Slideshow` for one source, `My Slideshow` for many, with auto-increment on collision) · FOLDER readonly textbox + Browse secondary button (default folder = first source folder, NOT `~/Videos`).

## Trial warning
Appears as an `AccentSoftBrush` block above the action row if either trial limit is exceeded.

## Action row
Back (QuietButton, left) · Create Slideshow (PrimaryButton, right).

---

# Rendering Screen Philosophy

The rendering screen should feel **calm, trustworthy, active, reassuring** — NOT technical, developer-focused, console-like.

Use human-friendly stages (no codec jargon, no FFmpeg, no raw logs):
- Preparing your photos...
- Creating video and adding transitions...
- Adding music...
- Saving your slideshow...
- Almost done...

## Layout
Centered Card containing:
- H1: "Creating your slideshow"
- Lead (centered): current stage label
- Overall progress bar (12 px, warm beige track + brand blue fill)
- Bottom row: PHASE-SPECIFIC photo progress (left, subtle) · "Overall progress: X%" (right, semibold)
- ETA text (centered, subtle, below)
- Cancel button (SecondaryButton, centered)

## Phase-specific photo progress wording

The left small text changes per stage so the user never sees identical "Photo X of Y" wording twice. Different verbs across stages make it obvious that each pass is doing different work, not repeating:

- Preparing your photos... → "Preparing photo X of N" → "All photos prepared"
- Creating video and adding transitions... → "Building frame X of N" → "All frames built"
- Adding music... → "Photos and frames complete"
- Saving your slideshow... → "Photos and frames complete"
- Almost done... → "Slideshow ready"

---

# Playback Philosophy

Playback should feel **simple, familiar, emotionally satisfying**. The generated slideshow is the emotional payoff moment. The UI should avoid distracting from the slideshow itself.

Supported controls only (no advanced media controls, editing tools, complicated menus):
- Play/Pause
- Replay
- Volume
- Position / time text
- Fullscreen
- Close

**As implemented:**
- Stage background: `#1A1714` (warm charcoal — NOT pure black)
- Control bar background: `#252220`
- Play/Pause: warm-accent primary pill (the primary control)
- Replay, Fullscreen, Close: dark transparent pills with hover fill
- Position scrubber: full-width across the top of the chrome, custom slim slider (4 px track, accent-bordered white circular thumb)
- Volume: matched slim slider, 120 px wide
- Custom `Style` resources scoped to `UserControl.Resources` in `PlaybackView.xaml`

---

# Interaction Philosophy

Interactions should feel immediate, responsive, predictable. Users should NEVER wonder "Did the app freeze?"

Use progressive thumbnail loading, visible progress, responsive scrolling. Avoid blocking UI, frozen windows, delayed visual feedback.

**As implemented:**
- Scanning + duplicate detection runs on background threads with progress events
- Render pipeline runs on background threads; UI marshals via `IProgress<T>` (Dispatcher posts handled by `System.Progress<T>`)
- Thumbnails are decoded lazily when the WrapPanel item realizes (WPF virtualization not explicitly enabled — TODO if review screens with 100+ groups feel slow)

---

# Motion & Animation

Subtle, soft, minimal. Avoid flashy transitions, excessive motion, dramatic animations.

EasyPhotoShow is calm utility software, NOT entertainment UI.

**As implemented:** No custom animations. ComboBox dropdown uses WPF's built-in `Fade` PopupAnimation. The 450ms hold-at-100% before navigating from Render → Completion is implicit motion that confirms completion.

---

# Workflow Restriction

DO NOT:
- redesign application workflow
- move major actions into menus
- change navigation structure
- introduce advanced editing systems
- add settings panels not specified

This specification is ONLY for visual refinement, emotional polish, layout quality, UI hierarchy, interaction feel.

---

# Final UX Principle

EasyPhotoShow should make users feel:

> "This is simple."
> "This feels safe."
> "I can do this."

The software should reduce stress, not add to it.

---

# Implementation Map

| Element | File |
|---|---|
| Theme (colors, brushes, type, button styles, ComboBox template) | `src/EasyPhotoShow.App/Styles/Theme.xaml` |
| Value converters (enum display, thumbnails, fraction→width, null→visibility) | `src/EasyPhotoShow.App/Converters/*.cs` |
| App icon assets (.ico + 256 PNG) | `src/EasyPhotoShow.App/Assets/easyphotoshow.ico`, `easyphotoshow_256.png` |
| Main screen | `Views/MainScreenView.xaml` · `ViewModels/MainScreenViewModel.cs` |
| Scanning screen | `Views/ScanningView.xaml` · `ViewModels/ScanningViewModel.cs` |
| Duplicate review screen | `Views/DuplicateReviewView.xaml` · `ViewModels/DuplicateReviewViewModel.cs` |
| Slideshow settings screen | `Views/SlideshowCreationView.xaml` · `ViewModels/SlideshowCreationViewModel.cs` |
| Rendering screen | `Views/RenderingView.xaml` · `ViewModels/RenderingViewModel.cs` |
| Completion screen | `Views/CompletionView.xaml` · `ViewModels/CompletionViewModel.cs` |
| Playback screen | `Views/PlaybackView.xaml` (with inline charcoal-theme resources) · `ViewModels/PlaybackViewModel.cs` |
| Navigation service | `Navigation/NavigationService.cs` |
| Session-scoped state | `Session/SlideshowSession.cs` |
| Trial limit constants | `Session/TrialLimits.cs` |
| Window shell (close-during-render warning) | `MainWindow.xaml`, `MainWindow.xaml.cs` |
