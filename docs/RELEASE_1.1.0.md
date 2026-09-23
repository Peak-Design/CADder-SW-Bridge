# CADder Bridge 1.1.0

Better rigs from your mates, and Refresh Model when there is a scene to
refresh. Use it with
[CADder 1.1.0](https://github.com/Peak-Design/CADder/releases/latest).

## Install

1. Close SolidWorks.
2. Run `CADder-Bridge-1.1.0-setup.exe` below. It replaces the version you
   have.
3. Update CADder in Blender to 1.1.0 too.

## New

- **Refresh Model is offered only when it can work.** The button stays
  gray until a running Blender holds a scene of the document, from a send
  or from a saved file opened again. It goes gray again when that Blender
  closes. A refresh goes to the Blender that holds the scene.
- **Refresh Model counts locked parts.** When Lock Geometry in CADder
  keeps the mesh of a part that changed, the message after the refresh
  says how many parts kept their locked geometry.

## Improvements and bug fixes

- **Automatic rig engine:** improvements and bug fixes to how the mates
  become joints. Parts that SolidWorks calls fully defined stay still, a
  flexible subassembly is read in its own document, and more loops,
  couplings, cams, paths and balls move as they do in SolidWorks.
- **Stability:** improvements to how the add-in works with SolidWorks
  during a send and a refresh.
- **Export STEP+:** improvements and bug fixes to appearance overrides,
  configurations and parts that the export has to copy.
- **Defeature:** improvements and bug fixes. Pins and bosses stay on the
  part.
- **Sending:** Escape stops an export before it writes the STEP file, and
  the progress bar stays until the send is done.
- **Installer:** the add-in is also registered for a SolidWorks year that
  you install after CADder Bridge.

If it gets your assembly wrong,
[open an issue](https://github.com/Peak-Design/CADder-SW-Bridge/issues) and
attach the file if you can.
