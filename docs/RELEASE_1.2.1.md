# CADder Bridge 1.2.1

A fix for face appearances in parts with several configurations. Use it with
[CADder 1.2](https://github.com/Peak-Design/CADder/releases/latest).

## Install

1. Close SolidWorks.
2. Run `CADder-Bridge-1.2.1-setup.exe` below. It replaces the version you
   have.
3. Use CADder 1.2.0 or later in Blender. CADder Bridge 1.2.1 works with
   every CADder 1.2.

The `.zip` holds the same add-in files for a manual install in SolidWorks.
It is not a Blender add-on, so do not install it in Blender.

## Bug fixes

- **Face appearances arrive in every configuration.** The configurations
  of a part can share one display state and have different dimensions.
  An appearance on faces, applied in one configuration, then did not
  arrive when you sent another configuration. Most of those faces came in
  with the appearance of the part. Each face now takes its appearance in
  the configuration that you send, in a part and in an assembly
  ([issue #1](https://github.com/Peak-Design/CADder-SW-Bridge/issues/1)).
