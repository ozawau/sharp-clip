# SharpClip

Windows clipboard manager with the same desktop behavior as Clipbara. It is free: there is no trial, purchase, or restore-purchases entry.

Ctrl+Shift+V opens a history panel at the bottom of the screen. Click a clip, or press Enter, to put it back. Clip Queue has no default hotkey; start it from the tray, or record one in Settings. Settings include language (follow system, English, 한국어, 简体中文), history limit, paste into the active app, plain-text paste, launch at login, excluded apps, and JSON import/export. The JSON matches Clipbara's backup.

iCloud sync and the iPhone keyboard are not part of this app. Windows cannot join Clipbara's CloudKit database.

## Build

Windows 10 version 2004 (build 19041) or later, the .NET 8 SDK, and the Windows App SDK build tools. The app targets the 21H2 API set.

```powershell
dotnet build src/SharpClip/SharpClip.csproj -c Release
```

The output is an unpackaged `SharpClip.exe`. This repository is authored on a Mac; the project compiles on Windows.
