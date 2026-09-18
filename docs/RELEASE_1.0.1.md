# CADder Bridge 1.0.1

Lighter meshes, and the same quality settings as Blender. Use it with
[CADder 1.0.1](https://github.com/Peak-Design/CADder/releases/latest).

## Install

1. Close SolidWorks.
2. Run `CADder-Bridge-1.0.1-setup.exe` below. It replaces 1.0.0.
3. Update CADder in Blender to 1.0.1 too.

## Changed

- **Draft, Balanced, Fine and Ultra cut a part as a STEP import does.**
  Draft was much denser than it needed to be: a 126-part engine goes
  from about 260,000 vertices to 134,000.
- **Custom and Relative Tessellation in Export Options.** Custom takes a
  distance and an angle. Relative Tessellation cuts each part to a share
  of its own size. These are the same settings, with the same names, as
  in Blender.
- **Parts with more than one body stay apart in Blender.** The bridge now
  tells Blender where each body starts, so two bodies that touch are not
  joined.

If it gets your assembly wrong,
[open an issue](https://github.com/Peak-Design/CADder-SW-Bridge/issues) and
attach the file if you can.
