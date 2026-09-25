---
title: CADder Bridge export of the active document, and the corpus sweep
applies to: development of the CADder Bridge: the manifest, STEP and mesh export that Blender asks for
tags: cadder, bridge, export, manifest, rig, joints, corpus, sweep, regression, swmesh, step
run: return CadderExport();
uses: cadder-bridge
tests: ..\..\..\test-assemblies\**\*.sldasm
---
## What it does

`CadderExport()` runs the export of the CADder Bridge on the active
document, the same code that answers the `export` request of Blender
(Rebuild from CAD). It writes the manifest, and the STEP file and the
direct-link mesh when the options ask for them. The files go to
`WorkDir\cadder-export\<document>`, so a corpus file does not change.

The result is what a change of the readers or the classifier shows in:

- `joint_shape`: the number of joints of each type.
- `components`, `rigid_groups`, `loops`, `mechanisms`, `warnings`.
- `joints`: each joint with its groups, confidence, coupling, and its
  limits in degrees and millimetres.
- `manifest_warnings`: the code of each warning and what it names.
- `limits_left_suppressed`: limit mates that the export could not put back.
- `_manifest`, `_step`, `_mesh`, `_log`, `_seconds`: not compared.

## Steps

1. Start a lab session (see `cadder-bridge`).
2. Open the document read-only and make it active.
3. Use `run` with recipe `cadder-export`. For other options, give code,
   for example:

```
return CadderExport(new CadderExportOptions { Mesh = true, Configuration = "Open" });
```

## The corpus sweep

The `tests` line gives `recipe_test` every assembly of `test-assemblies`
at the root of the repository, in all its folders. The corpus is not in
git, and the approved results stay next to this recipe in
`tests\cadder-export\baseline`, which is not in git either.

1. Change the readers or the classifier, and build the Bridge.
2. Start a lab session and use `recipe_test` with `cadder-export`.
3. The reply names the assemblies whose result changed, with the
   differences. A change that is correct: approve it (`approve` true, with
   `parts` for only some assemblies). A change that is not correct: a
   regression to fix.
4. To rig the manifests in Blender:
   `blender -b --factory-startup -P sw-addin\tools\rig_report.py -- <WorkDir>\cadder-export`.
