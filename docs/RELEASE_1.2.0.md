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
