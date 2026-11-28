# CADder Bridge 1.2.2

Fixes for appearances on subassemblies and for lids on gas stays. Use it
with [CADder 1.2.4](https://github.com/Peak-Design/CADder/releases/latest)
or later.

## Install

1. Close SolidWorks.
2. Run `CADder-Bridge-1.2.2-setup.exe` below. It replaces the version you
   have.
3. Use CADder 1.2.4 or later in Blender for the matching fixes of labels.
   CADder Bridge 1.2.2 works with every CADder 1.2.

The `.zip` holds the same add-in files for a manual install in SolidWorks.
It is not a Blender add-on, so do not install it in Blender.

## Bug fixes

- **Appearances on subassemblies arrive in Blender.** An appearance can be
  applied to a whole subassembly, or inside a subassembly to one of its
  components, and SolidWorks then shows it over the appearances of the
  parts. These appearances did not arrive, so the parts came in with their
  own appearance, often a plain white. CADder Bridge now reads them at
  every level of the assembly, and a higher level wins, as in SolidWorks.
  The same applies to an appearance on the whole top assembly.
- **A lid on gas stays opens the right way.** When the stay is mounted
  close to the line from the hinge, the lid passes a dead centre as it
  opens. The rig stopped at that point, so the lid opened only the other
  way. The rig now lets the lid pass it, as the mates in SolidWorks do.
