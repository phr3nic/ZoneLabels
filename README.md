# Zone Labeller

Zone Labeller is a Windows desktop tool for collecting full-screen computer-vision samples. Capture a screen, add pixel-anchored pins, label each pin with a zone, and export one crop per labeled pin.

## Features

- Captures the primary monitor using GDI `BitBlt`.
- Saves source images in `dataset/todo` so they can be labeled later.
- Supports multiple pins per image, zone hotkeys, and editable labels.
- Zooms and pans the preview while preserving source-pixel pin coordinates.
- Exports labeled crops to `dataset/data` and metadata to `dataset/labels.csv`.
- Moves completed source images and their pin metadata to `dataset/done`.
- Includes a capture library, settings dialog, fullscreen preview, and configurable shortcuts.

## Run

On 64-bit Windows, publish the self-contained executable using the command below and run `ZoneLab.exe` from the publish output. It does not require a separate .NET runtime installation. Keep it in a writable folder; settings and the `dataset` directory are created next to the executable.

Captures are intended for borderless-windowed applications. Exclusive fullscreen may not be visible to desktop capture.

## Build from source

Requirements: Windows and the .NET 8 SDK.

```powershell
dotnet build .\ZoneLab.csproj -c Release
dotnet publish .\ZoneLab.csproj -c Release -r win-x64 --self-contained true
```

The project uses WinForms and targets `net8.0-windows`.
