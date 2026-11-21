# svacitra

svacitra is a Windows photo organizer and non-destructive editor by Fajar Isnanto in Jakarta, Indonesia. Browse folders, compare edits side by side, add lettering and frames, and export photos or an MP4 slideshow.

## Install

1. Download the Windows x64 installer from [GitHub Releases](https://github.com/fajarisnanto/svacitra/releases/latest), or choose `svacitra.exe` to run the standalone app.
2. Run svacitra on a 64-bit Windows 10 or 11 PC. The executable includes .NET. The native editing window uses Microsoft Edge WebView2 Evergreen. If the runtime is missing, the app offers a browser option.
3. Choose **Add Folder** to index a photo folder. Double-click a photo to edit it. **Save Edits** stores the recipe in your catalog. **Export Copy** writes a JPEG with the edits applied.

Version 2.7.0 is unsigned. Windows application-control policies may block the installer or standalone executable. SHA-256 checksums are included on the release page.

## Contact and license

Copyright © 2026 Fajar Isnanto — Jakarta, Indonesia. Contact fajari@gmail.com.

The project is distributed under the [MIT License](LICENSE). See the [disclaimer](DISCLAIMER.txt) for the warranty, original-photo backup and cloud AI notices.

## Select and batch edit photos

Click the first thumbnail and Shift-click the last to select the entire range in gallery order. Shift-click another endpoint to extend or shorten it. Ctrl-click toggles individual photos. Ctrl+Shift-click adds another range. The photo tray shows the selected count.

Choose **Batch edit** above the gallery to set brightness, contrast, saturation, warmth, or an effect. Empty fields keep each photo’s existing settings. Choose **Apply to … photos** to save the adjustments in the catalog. Use **Export** to write edited copies.

## Review a shoot and deliver proofs

- **Compare & Pick** opens the selected sequence side by side in Edit Studio. Click an image to zoom both panes, then drag to inspect the same area. The right arrow advances the challenger. **Keep right photo** makes it the favorite. **Star favorite** saves the pick in Starred Photos.
- In Edit Studio, expand **Copy selected edits**, choose the adjustments to transfer, and click **Copy for pasting**. Select destination photos in the library, then choose **Paste edits**. The clipboard lasts for this app session.
- **Undo last batch** restores the recipes from before the most recent Batch edit, Paste edits, or tray Auto-enhance. The latest undo survives restarting the app. A photo edited again after the batch is skipped to preserve that newer edit.
- Select photos and choose **Export proof PDF**. Each page carries a sheet code and each photo has a number. When the client replies, open **Find proof numbers**, enter the code and numbers, and choose **Select originals**. The catalog remembers each sheet’s original file paths and selection order.

## Features

- Library: folders on disk, All / Starred / Luna AI Tagged / Recently Added, search, timeline, photo tray
- Edit Studio (double-click a photo): Fixes, Tuning, Effects, crop, straighten, hold-to-compare,
  filmstrip limited to 60 thumbnails of the current folder
- In Edit Studio, open **Design** to add a title, styled lettering, a frame, or the camera’s EXIF capture date and time. Save Edits keeps the design with that photo. Export makes a decorated copy.
- Edits are JSON recipes; originals are never modified. Export renders a full-resolution copy
- Properties / EXIF dialog (Info button, View > Properties, Ctrl+I): file details, camera, exposure, GPS,
  and every metadata tag. Exported and AI-generated JPEGs keep the original's EXIF (orientation reset,
  dimensions updated, old embedded thumbnail dropped); only JPEG sources carry EXIF over
- Open-source engines (both Apache-2.0, bundled in the exe):
  - **OpenCV** (OpenCvSharp4): Noise Reduction (non-local means), Clarity (CLAHE local contrast) and Sharpen
    (unsharp mask) sliders on the Tuning tab, and real Red-Eye removal on the Fixes tab. These have no CSS
    equivalent, so the editor asks the server for a re-rendered base image when a slider is released
  - **ImageMagick** (Magick.NET): opens HEIC/HEIF, AVIF and camera RAW (DNG, CR2, CR3, NEF, ARW, ORF, RW2,
    RAF, PEF, SRW). They are shown and exported as JPEG
- GPT-6 Luna AI: auto-tune, tagging, batch tagging, and prompt-based editing, with a fully offline
  heuristic engine when no API key is set or the cloud call fails
- Native menu bar (File / Edit / View / Tools / Help), Exit via menu, Ctrl+Q, Alt+F4, top-bar button or window close

## Solution layout

| Project | Purpose |
|---|---|
| `PicasaRevive.App` | WPF shell: menu bar + WebView2, single instance, shutdown |
| `PicasaRevive.Core` | SQLite catalog, scanner + folder watcher, SkiaSharp image pipeline, AI client, heuristics |
| `PicasaRevive.Server` | Kestrel on `127.0.0.1:<free port>`, `/api/*`, embedded `wwwroot` |
| `PicasaRevive.Tests` | xUnit: heuristics, clamping, catalog queries, image pipeline, JSON error contract |
| `wwwroot/` | HTML/CSS/JS frontend (from the Go reference, adapted) |

## Build and run

Requires the .NET 8 SDK. The WebView2 Evergreen runtime is preinstalled on Windows 11; if it is
missing the app offers a download link and a "open in my browser" fallback.

```
build.bat                      :: tests + publish\svacitra.exe
powershell -File publish.ps1   :: same, with -SkipTests / -ArtifactsPath options
```

Manual publish:

```
dotnet publish PicasaRevive.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish
```

`installer\PicasaRevive.iss` is an optional Inno Setup script.

## Data location

`%LOCALAPPDATA%\PicasaRevive` (catalog `picasa_data.db`, `.cache` thumbnails, `sample_library`).
Debug builds, or a file named `portable` next to the exe, keep data in `PicasaReviveData` beside the exe.
`PICASA_REVIVE_DATA` overrides both. A first run with an empty catalog generates a sample library.

## Offline folders and library backups

A rescan marks missing photos and folders **Offline** and keeps their edits, tags and stars.
Reconnect the drive and rescan to bring them back. Use **Remove from Library** to forget a folder explicitly.

Use **File > Back Up Library** to save photo records and watched folders to a .prlibrary file.
Keep separate copies of your original photos. **File > Restore Library** merges the backup into
the open library and offers a folder picker for missing roots. Matching photo paths use the edits
from the backup. Credentials and image files are excluded.

To build the executable and installer together, pass the path of ISCC.exe to publish.ps1 using
the -InstallerCompiler option. Setup reads its version directly from the published executable.

Automatic library backups are enabled daily. **File > Automatic Backup Settings** changes the interval
(minutes, hours, or days), destination, or enabled state and shows the last successful backup.
The app checks when it opens and while it runs. A missed interval produces one catch-up snapshot.
The default destination is the Backups folder inside the app data directory. Each library has
seven rotating .prlibrary files per destination. Open any of them with **File > Restore Library**.

## Export a slideshow

Start a slideshow and choose **Export MP4…** to save one pass through its photos, starting
with the slide on which playback was launched. The silent H.264 video is 1280×720 at 24 fps,
with saved edits, fades and 3.5 seconds per photo. Export needs the source files and the Windows
Media Foundation H.264 encoder. Cancelling leaves the destination file intact.

## AI configuration

Top bar > Luna AI (or Tools > Luna AI Settings): model, OpenAI-compatible endpoint, your own API key, Test Connection.
Leave the key empty to stay fully offline.

- **Default: OpenAI GPT-Image-2.5** (`gpt-image-2.5-flare`, or `gpt-image-2.5-sunburst`). With a key set,
  a prompt in Edit Studio sends the photo (max 2048 px, saved edits applied) to `POST {endpoint}/images/edits`
  and saves the result as `name_luna.jpg` next to the original, which is never changed. Each image is billed
  to your key and there is no confirmation: every run saves another copy (`name_luna_2.jpg`, ...). Auto-tune and tagging stay on the offline
  engine, because image models return pictures, not text. Test Connection only checks the key and model.
- **Chat vision models** (e.g. `gpt-4o`) return slider settings instead; `/chat/completions` is appended
  when missing, and on `api.openai.com` the old name `gpt-6-luna` is routed to `gpt-4o`.
AI endpoints always answer with JSON; on failure they return
`{ "success": false, "error": "...", "fallback": { ... } }` so the UI can offer the offline engine.
The AI key is encrypted with Windows DPAPI for the current user and stored under HKCU\Software\PicasaRevive\AiKeys. The catalog holds the endpoint, model and a credential reference. Moving the executable or copying a migrated library to another Windows account does not transfer the key. Existing catalog keys migrate when the updated app opens the library. Old backups can still contain the previous key. Clear the key field and save to remove the active registry credential.

## Format and performance notes

- Cloud-backed folders must finish downloading each original before the first full render. The editor caches screen-size previews locally.
- RAW files are decoded at full resolution when producing thumbnails. Indexing large RAW collections can take time.
- Red-eye correction looks for round red regions and can also affect similar objects elsewhere in a photo.

## License

See the [MIT License](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.txt). Third-party: SkiaSharp (MIT), Microsoft.Data.Sqlite (MIT),
MetadataExtractor (Apache-2.0), System.Drawing.Common (MIT), OpenCV / OpenCvSharp4 (Apache-2.0),
ImageMagick / Magick.NET (Apache-2.0), WebView2 (Microsoft).
