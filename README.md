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
- **Customizable Launchpad**: pin apps to the top, drag to rearrange, rename, hide, change icons, add your own apps.
- **Hot Corners**, **screenshot shortcuts** (Ctrl+Shift+3 / 4 / 5 with a floating preview), **Force Quit** (Ctrl+Alt+Esc) and **About This PC**.
- **Dock editing like a Mac**: drag icons to reorder, drag one off the Dock to remove it.
- **Settings** window for dock size, magnification, auto-hide, clock format, light/dark, accent colour, glass frost/tint/refraction/shine, and the apps in your dock.

## Install

1. Download **`MacLook.zip`** from the [latest release](https://github.com/KingCreeper531/MacLook/releases/latest) and extract it.
2. Run **`MacLookSetup.exe`** and click **Install**. It installs just for you (no admin prompt), adds Start menu and Desktop shortcuts, and can open Mac Look at sign-in.

Prefer no installer? Download `MacLook.exe` instead and run it from anywhere.

Windows SmartScreen may warn you because the app isn't code-signed. Choose **More info → Run anyway**.

Updates install automatically. You can turn that off, or check by hand, in **Settings → General**.

**Uninstall:** Windows Settings → Apps → Installed apps → Mac Look → Uninstall. Your taskbar and wallpaper are put back.

Silent options for scripting: `MacLookSetup.exe --silent [--no-launch]` and `MacLook.exe --uninstall --quiet`.

## Quitting and the Windows taskbar

Mac Look hides the Windows taskbar while it runs and puts it back when you quit (brush icon in the menu bar → **Quit Mac Look**). If it ever gets force-closed and the taskbar stays hidden, run:

```
MacLook.exe --restore
```

## Antivirus warnings

Mac Look is not code-signed yet, so a few antivirus engines may flag it as "unknown" (it hides the taskbar, watches the Windows key and takes screenshots for the glass effect, which heuristic scanners sometimes dislike). All the source code is in this repo, and you can build it yourself with `build.ps1`.

## Fully see-through File Explorer

Mac Look only changes other apps' windows through public Windows APIs (title bar material, corners and borders). To make the *inside* of File Explorer translucent, use the **Translucent Windows** mod in [Windhawk](https://windhawk.net/).

## Build from source

No SDK needed. This uses the C# compiler that ships with Windows (.NET Framework 4.x):

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

`MacLook.exe` and `MacLookSetup.exe` are written next to `build.ps1`. Settings, the dock list and custom icons live in `%AppData%\MacLook`.

## Releasing an update

1. Bump the version in `src/Program.cs` (`AssemblyVersion`).
2. Build, then publish a GitHub release whose tag is the new version (for example `v2.3.0`) with both `MacLook.exe` and `MacLookSetup.exe` attached. The updater downloads `MacLook.exe`.

Running copies pick it up within a few hours, or right away with **Check for Updates**.

## Credits

Some interaction ideas (spring dock, elastic slider, glare hover, blur-in) were inspired by [React Bits](https://github.com/DavidHDev/react-bits) and re-implemented natively. All artwork is drawn by Mac Look itself, and app icons come from the apps installed on your PC.

Mac Look is an independent project and is not affiliated with or endorsed by Apple Inc. macOS is a trademark of Apple Inc.
