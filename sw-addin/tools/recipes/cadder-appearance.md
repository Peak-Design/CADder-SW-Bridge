---
title: CADder Bridge appearance checks: a dump of appearances and decals, and an appearance put on a model
applies to: development of the CADder Bridge: the appearances and textures of the direct link
tags: cadder, bridge, appearance, render material, decal, texture, mapping, p2m, colour, uv
run: return CadderAppearances();
uses: cadder-bridge
---
## What it does

- `CadderAppearances(maxFaces, face)`: a dump of the appearances of the
  active document. Each render material and decal of the model and of each
  part that it uses, with the entities of each, and per face the colour
  values, the texture coordinates and the decal properties. In a part,
  `face` gives the full triangles and texture coordinates of that face.
  Reads only. This is the evidence the appearance export is built against.
- `CadderApplyAppearance(options)`: puts a library appearance (`.p2m`) on
  the active part (the document, its first body or one face), or in an
  assembly on one component or one face of it. In memory: nothing is
  saved. Use it to put a known texture on a model and compare SolidWorks
  with Blender.

## Steps

1. A lab session with the document open and active (see `cadder-bridge`).
2. Use `run` with recipe `cadder-appearance`, or give code:

```
return CadderAppearances(60, 2);
return CadderApplyAppearance(new CadderAppearanceOptions { Path = @"C:\...\texture.p2m", Target = "face:2", Width = 0.01, Height = 0.01 });
return CadderApplyAppearance(new CadderAppearanceOptions { Path = @"C:\...\red.p2m", Component = "sub-1/part-2", Target = "component" });
```

Set `undo_label` on a run that puts an appearance on a model.

## Traps

- Do not change a decal through the API in a lab session. A new decal
  position and a redraw crashed SolidWorks 2022 at each try (2026-09-15).
- An appearance on a face in an assembly: SolidWorks stored nothing for
  `AddRenderMaterial` with a face of a component, also with the face that
  a ray picks (2026-09-24). The path of the Bridge that reads such an
  appearance is not tested live.
