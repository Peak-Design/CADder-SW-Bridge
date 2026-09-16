# CADder Bridge: the SolidWorks add-in

The add-in reads the mates of the open assembly, works out the joints they
leave free, and sends the geometry and a rig manifest to Blender. Blender
builds an armature from the manifest through the CAD Link tab of
[CADder](https://github.com/Peak-Design/CADder). The manifest
contract lives in [../schema](../schema) and outranks both halves.

## Install

You need SolidWorks 2022 or newer and Blender 5.1 with CADder.

1. Run the installer from the
   [Releases](https://github.com/Peak-Design/CADder-Bridge/releases) page.
   It copies the add-in to `Program Files\Peak Design\CADder Bridge` and
   registers it with every SolidWorks year on the machine.
2. Start SolidWorks. Open **Tools > Add-Ins** and tick **CADder Bridge** in
   both columns if it is not ticked already.
3. In Blender, open **Edit > Preferences > Add-ons > CADder** and tick
   **CAD Link (experimental)**.

Without the installer: build the Release DLL and run
`src\Peak.Cadder\Register-Addin.bat`. The script asks for
administrator rights, because the add-in keys live under HKLM.

## Use

Open an assembly. The **CADder Bridge** tab on the ribbon has three
buttons, and two more when "Show the advanced commands" is ticked in
Export Options:

| Button | What it does |
|---|---|
| Send to Blender | Tessellates the parts in SolidWorks and sends them with the rig manifest to the running Blender: geometry, appearances, rig, parenting. Starts Blender when none is running. Blender can ask for a finer mesh later. |
| Export Options | What a send carries and how Blender receives it. The first group is the send itself: hierarchy, mesh quality, up axis, one object per solid body, only the selected components, and how much of the SolidWorks appearances travels (the appearances, the decals, the texture mapping). The STEP+ group appears with the advanced commands. The last groups say what Blender does after the import and which Blender to start. |
| Refresh Poses | Moves the parts in Blender to where they are now in SolidWorks. It reads no mates and writes no files, so it answers in about a second on an assembly a send takes minutes over. Send the assembly first, and send it again when parts are added or removed. |
| Export STEP+ (advanced) | Writes a STEP file with the appearance and engineering-material repairs, no rig. |
| Export Rig (advanced) | Writes only the rig manifest to disk. |

An assembly with mate errors or over-defined mates cannot give a
trustworthy rig, because SolidWorks itself decides which mate of an
over-defined set to ignore. The export names the mates and asks: send
or write the geometry with no rig, or stop and fix the mates.

Only the top-level mates and the mates inside flexible subassemblies count.
A rigid subassembly is one body, exactly as it is in the viewport. This is
on purpose: the rig moves what you can drag in SolidWorks, and nothing else.

## Files the add-in writes

- Exports go into a per-assembly folder under
  `%LOCALAPPDATA%\PeakDesign\CADder\exports`, or next to the assembly
  when Export Options says so.
- Settings: `%APPDATA%\PeakDesign\CADder\settings.json`.
- Log: `%LOCALAPPDATA%\PeakDesign\CADder\cadder-debug.log`. The
  log rotates at 8 MB. Attach it to a bug report.

## Build

```
dotnet build src\Peak.Cadder -c Release
dotnet test tests\Peak.Cadder.Tests
```

The build needs the SolidWorks interop assemblies and looks for them in
every SolidWorks year from 2022 up. Pass `-p:SolidWorksApiDir=<path to
SOLIDWORKS\api\redist>` when the probe misses your installation. The
interop types are embedded, so the built DLL ships alone.

Close SolidWorks before a Release build: SolidWorks holds the registered
DLL open.

The ribbon icons are 128 px PNG masters in `icons\` at the repository
root (`send_direct`, `options`, `step+`, `export_rig`, `logo`, and
`refresh` for a later button). `tools\Make-Icons.py` (Pillow) scales them
to the six sizes SolidWorks asks for and stitches the command strips into
`src\Peak.Cadder\icons\`. Edit a master, run the script, and the
build copies the strips next to the DLL.

## Test harness

The add-in listens on localhost so a test harness can drive SolidWorks
without the ribbon. Requests that change the open model (open, close,
suppress, set a dimension, rebuild, quit) run only while **Test harness**
is ticked in Export Options. It is off by default. Nothing saves a
document. See [tools/README-swlab.md](tools/README-swlab.md).

## Licence

MIT, see [LICENSE](LICENSE). The SolidWorks interop assemblies belong to
Dassault Systèmes and are not part of this licence:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
