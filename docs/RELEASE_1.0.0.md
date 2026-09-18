# CADder Bridge 1.0.0

The first release. CADder Bridge is the SolidWorks add-in that sends an
assembly to Blender with one button. It needs
[CADder 1.0.0](https://github.com/Peak-Design/CADder/releases/latest) in
Blender 5.1.

## Install

1. In Blender, install CADder and tick **SolidWorks Bridge** in its
   preferences.
2. Close SolidWorks, then run `CADder-Bridge-1.0.0-setup.exe` below.
3. Start SolidWorks. The **CADder Bridge** tab is on the ribbon.

The installer is not signed yet, so Windows may show **Windows protected
your PC**. Click **More info**, then **Run anyway**. To remove the add-in,
use **Settings > Apps > Installed apps**.

Needs Windows 10 or 11 (64-bit) and SolidWorks 2022 or newer.

## What it does

- **Send to Blender** sends the open assembly: geometry, appearances,
  decals, the tree and a rig built from the mates. It starts Blender if
  none is running.
- **Refresh Model** brings the Blender scene up to date with the
  assembly, part by part, and asks what to do with the rig.
- **Export Options** sets what a send carries: mesh quality, triangles to
  quads, compound surfaces unwrapped, appearances, only the selected
  components, one object per body, and whether Blender turns to the
  SolidWorks view.
- The rig follows the mates: joints and limits, gears, rack and pinion,
  screws, cams, symmetry, flexible subassemblies and closed loops.
- An assembly with mate errors or over defined mates is named before the
  send, and you choose: send without a rig, or stop and fix the mates.

## For the best rig

What moves in SolidWorks is what moves in Blender. Fully define the
assembly, fix mate errors, lock your fasteners, and make flexible the
subassemblies that should move.

## Known limits

- A universal joint **mate** turns 1:1, so its timing within a turn is
  approximate. A universal joint built from its parts is exact.
- Spherical and cylindrical texture mappings are approximate. Planar and
  automatic match SolidWorks.
- Component patterns (linear, circular, chain) are not read yet.

This is a first release, tested against a set of assemblies built for the
purpose. If it gets yours wrong,
[open an issue](https://github.com/Peak-Design/CADder-SW-Bridge/issues) and
attach the file if you can.
