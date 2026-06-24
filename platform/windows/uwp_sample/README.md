# Godot-in-UWP — sample app

A minimal, working UWP (AppContainer) application that hosts the Godot engine
**in-process** and renders into a XAML `SwapChainPanel`, using the embedding added
to this engine fork (see [`../../../UWP_EMBEDDING.md`](../../../UWP_EMBEDDING.md)).

## Demo

[**demo.mp4**](demo.mp4) — multiple spinning cubes on a dark plane with one UWP
button per cube. Clicking a button recolors that cube (the color is chosen in
Godot), makes it hop so it is obvious which one changed, and the new color is
pushed back over the host↔engine bus into the message box. Left-drag orbits the
camera.

The bundled Godot project (`GodotUWPSample/GodotProject/`) renders multiple
spinning cubes on a dark plane and exercises the two-way host↔engine message
bus. It proves rendering, DPI-correct sizing, live resize, host→engine input
injection, and host↔engine messaging.

```
uwp_sample/
├── GodotUWPSample.sln
├── GodotUWPSample/              C# UWP app (namespace Godot.Uwp.Embedding)
│   ├── Godot/                   GodotNative (P/Invoke), GodotEngineHost (engine thread),
│   │                            EngineMessageReceiver / EngineMessageSender (the bus)
│   ├── MainPage.xaml(.cs)       SwapChainPanel + input/resize/DPI/lifecycle wiring
│   └── GodotProject/            the bundled Godot project (multi-cube + bus sample)
├── GodotUWPSample.Package/      Windows Application Packaging project (VS F5 deploy)
├── INTEGRATION_GUIDE.md         step-by-step recipe (engine + project + app)
└── EMBEDDING_ARCHITECTURE.md    architecture, package & sequence diagrams, bus protocol
```

## Prerequisites

1. **Build the engine DLL first** — follow [`../../../UWP_EMBEDDING.md`](../../../UWP_EMBEDDING.md):
   ```powershell
   scons platform=windows target=template_release arch=x86_64 ^
         library_type=shared_library debug_symbols=yes disable_path_overrides=no
   ```
   This produces `bin/godot.windows.template_release.x86_64.dll` at the repo root.
   The sample's `.csproj` references it (and `D3D12Core.dll` / `d3d12SDKLayers.dll`)
   from `..\..\..\..\bin\` and copies them into the package.
2. **Visual Studio** with the UWP workload, and a Windows 10/11 SDK matching the
   `.csproj`'s `TargetPlatformVersion` (adjust it to one you have installed).

## Run it

**From Visual Studio (easiest):** open `GodotUWPSample.sln`, set
`GodotUWPSample.Package` as the startup project, choose **x64**, and press **F5**.

**From the command line (Release):**
```powershell
$msbuild = "<VS>\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild GodotUWPSample\GodotUWPSample.csproj /restore /p:Configuration=Release /p:Platform=x64
# the deployable layout is the ilc\ subfolder (.NET Native output)
Add-AppxPackage -Register "GodotUWPSample\bin\x64\Release\ilc\AppxManifest.xml"
# launch by its package-family-name!App
```

Logs (engine output + `[Bus]` traffic) are written to
`%LOCALAPPDATA%\Packages\<package-family-name>\LocalState\Logs\godot_*.log`.
Expect `display server: embedded`, `D3D12 … Using Device`, `Engine running`.

## Swapping in your own Godot project

Export your project as a `.pck` (Windows Desktop preset) to
`GodotUWPSample/Assets/project.pck` — the app auto-detects it and boots it via
`--main-pack`; otherwise it loads the loose `GodotProject/` folder. The engine
version must match the editor that exported the `.pck`. See `INTEGRATION_GUIDE.md`
for the project-side bridge (the `UWPHost` message bus) and the gotchas
(MAX_PATH/MRT with loose folders, GDExtension DLLs shipped loose, etc.).

> **Note on the bundled `.csproj`:** it targets old-style UWP / .NET Native and
> was developed against a specific Windows SDK and package identity. You will
> likely need to adjust `TargetPlatformVersion`, the package `Identity`/Publisher
> in `Package.appxmanifest`, and signing to match your environment.
