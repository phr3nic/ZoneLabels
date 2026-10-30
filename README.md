# Zone Labeller

Zone Labeller is a Windows desktop tool for collecting labelled image crops: capture the primary display, place pins on targets, assign each pin a zone, and export one 96 × 96 crop per pin to `dataset/data` with rows recorded in `dataset/labels.csv`.

## Screenshots

![Labelling screen](docs/screenshot-labelling.png)

![Settings screen](docs/screenshot-settings.png)

## Requirements

- 64-bit Windows only.
- The .NET 8 SDK is needed only to build from source. The published `win-x64` application is self-contained and does not require a separate .NET runtime.

## Quick start

1. Download and extract the `win-x64` release.
2. Run `ZoneLabels.exe` from a writable folder.
3. Press the configured capture key (default `Alt+Q`). Bindings without Alt work only while the canvas has focus; Alt bindings are global, but are suppressed during settings and text entry. Alt capture also requires the pointer outside the ZoneLab window.
4. Place pins, assign zones, and press **Done** when the capture is complete. ZoneLab saves the capture and opens the next todo image when one is available.

The app generates `config.json` beside the executable on first run. The `dataset` folder and `capture.log` are also stored beside it. Captures are intended for borderless-windowed applications; exclusive fullscreen may not appear in a desktop capture.

To build and publish from source, install the .NET 8 SDK and run `.\build.ps1` in PowerShell. The commands can also be run separately:

```powershell
dotnet build .\ZoneLab.csproj -c Release
dotnet publish .\ZoneLab.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

## How to label

- Each pin exports the 96 × 96 source pixels centered on the pin. Pixels beyond an image edge are black-padded so every exported crop remains 96 × 96.
- Keep pins at least 40 pixels apart. If two pins are within the configured `MinPinSpacing`, the app marks both with dashed rings and shows a warning. This is a warning only and does not prevent export.
- Use one pin for a small target. Multiple pins on one small target create duplicate or overlapping samples.
- When magnified, move the pointer to move the centered aim marker across the image. Left-click pins the marker location; the crop preview shows the 96 × 96 output.
- Assign each pin a zone using its configured button or hotkey. **Ctrl+1** through **Ctrl+6** directly assign the first six configured zones to the last pin. **Save** stores pin progress without exporting. **Done** exports pending crops, moves the capture and pin metadata into `dataset/done`, and opens the next todo image when available.
- **Discard** asks for confirmation; double-click it to discard immediately. **Ctrl+Z** removes only the most recently placed pin on the current image.
- The strip along the bottom shows `zone count/target`, based on rows in `dataset/labels.csv`. **Refresh counts** rereads the CSV.

## Settings reference

`config.json` uses JSON camel-case property names. Defaults are shown below.

| Key | Default | Description |
|---|---|---|
| `captureKey` | `"Alt+Q"` | A-Z or number-row 0-9 shortcut for capturing the primary display. Without Alt it works only while ZoneLab is focused; with Alt it works globally only while the pointer is outside the ZoneLab window. Other modifiers may also be used. |
| `discardKey` | `"M"` | Shortcut for removing all pins from the current image. It is suppressed while text entry has focus. |
| `zones` | `head` (`1`), `chest` (`2`), `body` (`3`), `arms` (`4`), `legs` (`5`), `none` (`6`) | Zone names and their assignment hotkeys. Each entry has `name` and `key`. Use **Add values** in Settings to add labels with a goal defaulting to 500. |
| `alwaysOnTop` | `false` | Keep the main window above other windows. |
| `hideWhileCapturing` | `false` | Hide the app briefly while capturing the display. |
| `doneRetentionDays` | `7` | Age in days after which files in `dataset/done` are purged at startup. |
| `targetCounts` | `head: 500`, `chest: 500`, `body: 500`, `arms: 500`, `legs: 500`, `none: 500` | Per-zone goals shown in the bottom count strip and editable in Settings. |
| `minPinSpacing` | `40` | Minimum pin separation in source-image pixels for the warning. It does not block exports. |
| `windowX` | `null` | Saved window left position in screen pixels. `null` uses the centered default. |
| `windowY` | `null` | Saved window top position in screen pixels. `null` uses the centered default. |
| `windowWidth` | `null` | Saved window width in screen pixels. `null` uses the default size. |
| `windowHeight` | `null` | Saved window height in screen pixels. `null` uses the default size. |

`doneRetentionDays` must be between 0 and 3650; `minPinSpacing` must be between 0 and 10000. Target counts must be non-negative integers. Crop output is fixed at 96 × 96 pixels.

The same shortcut format applies to `discardKey` and each zone's `key`; those shortcuts may use any non-modifier key with optional modifiers. Without Alt, shortcuts are handled only while the canvas has focus. Alt shortcuts are registered globally and suppressed during settings or text entry. The capture key is restricted to A-Z and number-row 0-9. `Ctrl+Z` is reserved for undoing the most recently placed pin.

## Dataset folder layout

| Path | Contents |
|---|---|
| `dataset/todo/` | Captured images and JSON pin state waiting to be labelled. |
| `dataset/done/` | Completed captures and their JSON pin state; old files are removed by the retention setting. |
| `dataset/data/` | Exported 96 × 96 PNG crops. |
| `dataset/labels.csv` | Crop filename, zone, crop dimensions, and source-screen coordinates. |
| `dataset/capture.log` | Application errors and done-purge counts. |

The application does not purge `dataset/data` or modify existing rows in `dataset/labels.csv`.

## Troubleshooting

- **A capture is blank or missing:** use a borderless-windowed application; exclusive fullscreen may not be captured by Windows desktop capture.
- **A shortcut does not work:** another application may own the global shortcut. Check the status message and assign a different key in Label settings.
- **Settings or captures cannot be saved:** move the application to a folder where the current user can write files, then check `dataset/capture.log`.
- **Counts are unexpected:** use **Refresh counts** to reread `dataset/labels.csv`. Missing CSV files display zero.
- **A crop looks clipped:** the crop is centered on its pin; pixels beyond the source image are black-padded.

License: [MIT](LICENSE).
