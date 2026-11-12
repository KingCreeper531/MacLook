# Mac Look

A macOS-style desktop for Windows 11, rendered in liquid glass. One small `.exe`, no installer, and it keeps itself up to date.

## Features

- **Menu bar** with the active app's name, File / Edit / View / Window / Help menus that drive the app you're in, Wi-Fi and Sound drop-downs, Spotlight, Control Center and a clock.
- **Dock** with spring-physics magnification, bounce on launch, open-app dots, Downloads and Recycle Bin. It auto-hides over your windows, pops up when your mouse touches the bottom edge, and stays up when nothing covers it.
- **Liquid glass** everywhere: the dock, menu bar, menus and panels frost and bend what's behind them, with curved, refracting edges and light catching the rim.
- **Launchpad**: tap the Windows key for a full-screen grid of every installed app, with search.
- **Mission Control** (Ctrl+Alt+Up): every open window as a live thumbnail that flies out from where it really is.
- **Spotlight** (Alt+Space): apps, a calculator and web search.
- **Control Center** and **Notification Center** with volume, dark mode, media controls, a calendar and more.
- **Premium icons**: every app icon on a glossy rounded plate, or set your own PNG per app (right-click → Change Icon…).
- **Glass window frames**: frosted title bars and rounded corners on every app window, including File Explorer.
- **Window animations**: a genie effect when you minimize into the dock, and a fade when you close.
- **Settings** window for dock size, magnification, auto-hide, clock format, light/dark, accent colour, glass frost/tint/refraction/shine, and the apps in your dock.

## Install

1. Download `MacLook.exe` from the [latest release](https://github.com/KingCreeper531/MacLook/releases/latest).
2. Put it somewhere permanent (for example `Documents\MacLook`) and double-click it.

Windows SmartScreen may warn you because the app isn't code-signed. Choose **More info → Run anyway**.

Updates install automatically. You can turn that off, or check by hand, in **Settings → General**.

## Quitting and the Windows taskbar

Mac Look hides the Windows taskbar while it runs and puts it back when you quit (brush icon in the menu bar → **Quit Mac Look**). If it ever gets force-closed and the taskbar stays hidden, run:

```
MacLook.exe --restore
```

## Fully see-through File Explorer

Mac Look only changes other apps' windows through public Windows APIs (title bar material, corners and borders). To make the *inside* of File Explorer translucent, use the **Translucent Windows** mod in [Windhawk](https://windhawk.net/).

## Build from source

No SDK needed. This uses the C# compiler that ships with Windows (.NET Framework 4.x):

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

`MacLook.exe` is written next to `build.ps1`. Settings, the dock list and custom icons live in `%AppData%\MacLook`.

## Releasing an update

1. Bump the version in `src/Program.cs` (`AssemblyVersion`).
2. Build, then publish a GitHub release whose tag is the new version (for example `v2.3.0`) with `MacLook.exe` attached.

Running copies pick it up within a few hours, or right away with **Check for Updates**.

## Credits

Some interaction ideas (spring dock, elastic slider, glare hover, blur-in) were inspired by [React Bits](https://github.com/DavidHDev/react-bits) and re-implemented natively. All artwork is drawn by Mac Look itself, and app icons come from the apps installed on your PC.

Mac Look is an independent project and is not affiliated with or endorsed by Apple Inc. macOS is a trademark of Apple Inc.
