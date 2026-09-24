# CADder Bridge 1.2.0

Configurations. Each configuration of an assembly now goes to Blender as a
send of its own, with its own collection and its own rig, and you can send
several configurations at once. Use it with
[CADder 1.2.0](https://github.com/Peak-Design/CADder/releases/latest).

## Install

1. Close SolidWorks.
2. Run `CADder-Bridge-1.2.0-setup.exe` below. It replaces the version you
   have.
3. Update CADder in Blender to 1.2.0 too. CADder Bridge 1.2 does not work
   with CADder 1.1.

## New

- **Each configuration is a send of its own.** The files of a send are
  named after the document and the configuration,
  `<document>_<configuration>`, and Blender puts each configuration in a
  collection of that name, with its own rig. Send one configuration,
  switch to another and send again: Blender holds both, side by side.
- **Send several configurations at once.** Select **Multiple
  configurations** under **Send to Blender** in Export Options. **Send to
  Blender** then asks which configurations to send. SolidWorks shows each
  one in turn and exports it, and shows the configuration you had active
  again at the end. The list remembers what you selected for each
  document until SolidWorks closes.
- **Append as a new copy.** With this option in Export Options, a send
  puts the assembly in Blender again as a copy, beside the send that is
  there, with its own collection and rig. Refresh Model brings the send
  and its copies up to date.
- **Link identical parts.** Blender gives a part the mesh of the same part
  that is already in the scene, from any assembly: the same shape with the
  same appearance. The option is on. Turn it off to give each send meshes
  of its own.
- **Refresh Model asks which configurations to refresh** when Blender
  holds more than one configuration of the document. The configurations
  Blender holds are selected. When Blender holds one configuration,
  Refresh Model refreshes that one, also when another one is active.
- **Requests from Blender name the configuration.** Rebuild from CAD,
  Defeature and the pose push get the parts and the poses of the
  configuration that Blender holds, not of the one that is active.
- **One failure does not stop a send of several configurations.** The
  message at the end gives the result of each configuration.

## Changes

- The files of a send are named `<document>_<configuration>` and no longer
  `<document>`. So is the collection in Blender, which was
  `<document>_Top_Level`.

## Bug fixes

- **An appearance applied in the assembly now arrives in Blender.** An
  appearance on a part that sits at the top of the assembly, applied in
  the assembly and not in the part, came to Blender as the appearance of
  the part. That part also shared its mesh with the unpainted placements
  of the part.
