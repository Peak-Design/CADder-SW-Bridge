# CADder Bridge

**Export a SolidWorks assembly as a rigged, posable model in Blender.**

The tool is two programs joined by one file:

1. **`sw-addin/`. Peak.Cadder** (C#, SolidWorks add-in, MIT). Reads the
   mates of the open assembly, merges components with no relative freedom into
   rigid groups, classifies the residual freedom between groups as joints, and
   writes a STEP file with a rig manifest (`<name>.rig.json`) beside it.
2. **The `rig/` subpackage of
   [CADder](https://github.com/Peak-Design/CADder)** (Python,
   Blender 5.1+, GPL-3.0-or-later). Reads the manifest, builds an armature:
   one bone per rigid group, constraints from the joint limits, and parents
   the imported STEP geometry to the bones. It lives in the importer's repo
   ([PLAN.md](PLAN.md) D10); this repo holds the SolidWorks half and the
   contract.

The manifest is the contract between them.
[`schema/rig-manifest.schema.json`](schema/rig-manifest.schema.json) fixes its
shape and [`schema/SCHEMA.md`](schema/SCHEMA.md) fixes its meaning: metres and
radians, the assembly's global right-handed Z-up frame, limits as absolute
mate values plus `value_at_rest` (consumers pose in deltas from the rest
pose), kinematic loops pre-cut by the exporter. Either half can be replaced by
any program that honours the contract.

## Scope: what you see is what you get

The exporter analyses the mates of the open top-level assembly, plus the
internal mates of subassemblies set to solve as **Flexible** (recursively). A
rigid subassembly is one leaf body, whatever moves inside it. What moves in
SolidWorks is what gets a bone; nothing else does.

## Status

**M1 in progress.** Nothing is released yet. The milestones and their
acceptance criteria are in [PLAN.md](PLAN.md); the corpus of test assemblies
that backs them is specified in [test-assemblies/](test-assemblies/).

## Repository layout

| Path | Contents |
|---|---|
| `schema/` | The manifest contract: JSON Schema, semantics, golden examples |
| `sw-addin/` | SolidWorks add-in (C#, net48, MIT) |
| `sw-addin/vendor/sw2urdf/` | Reference copies of the vendored SW2URDF files (MIT, not compiled) |
| `test-assemblies/` | Build recipes for the test corpus: the `.SLDASM` files stay out of the repo |
| `.github/workflows/` | Schema/example validation; the SolidWorks half builds locally only |

## Quick start. SolidWorks add-in

Users: run the installer from the Releases page, then start SolidWorks. The
details are in [sw-addin/README.md](sw-addin/README.md).

Developers need SolidWorks 2022 or newer (for the interop assemblies) and
the .NET SDK. The build fails with a clear message when it cannot find the
interops. Point it at them with `-p:SolidWorksApiDir=...`.

```
dotnet build sw-addin/src/Peak.Cadder/Peak.Cadder.csproj -c Release
sw-addin/src/Peak.Cadder/Register-Addin.bat
```

Registration writes to HKLM, so the script asks for administrator rights.
Start SolidWorks and tick **CADder Bridge** in *Tools → Add-Ins* if it is not
already ticked. The export command writes `<assembly>.step` and
`<assembly>.rig.json` side by side.

## Quick start. Blender side

Install (or update) **CADder** and tick **CAD Link (experimental)**
in its add-on preferences. The **CAD Link** tab appears in the 3D View
sidebar. **Send to Blender** in SolidWorks then imports and rigs the
assembly in one step. The manual route: point the tab's Manifest field at
a `.rig.json` with the STEP file beside it, exactly as the exporter wrote
the pair, and press the Rig buttons in order.

## Licences

The two halves are licensed separately, and the split is deliberate:

- `sw-addin/` is **MIT** ([sw-addin/LICENSE](sw-addin/LICENSE)). The MIT
  licence does not cover the SolidWorks interop assemblies, which are the
  property of Dassault Systèmes, see
  [sw-addin/THIRD-PARTY-NOTICES.md](sw-addin/THIRD-PARTY-NOTICES.md).
- The Blender half is **GPL-3.0-or-later**, as Blender add-ons that use
  `bpy` must be; it lives in the CADder repo and carries that repo's
  licence.

The JSON manifest is the firewall between the two domains. The halves share no
code and communicate only through a documented file format, so the GPL side
never links the MIT side and the MIT side carries no GPL code. Keep it that
way: code moves across the boundary in neither direction, only the contract in
`schema/` does.
