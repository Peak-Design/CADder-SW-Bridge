---
title: CADder Bridge geometry checks: small features, closure after a defeature, texture coordinates
applies to: development of the CADder Bridge: the defeature and the mesh of the direct link
tags: cadder, bridge, defeature, small features, closure, open edges, tessellation, texture, uv, plane, fill, cap
run: return CadderSmallFeatures();
uses: cadder-bridge
---
## What it does

All methods only read. They open, change and write nothing.

- `CadderSmallFeatures(options)`: for each body of each part of the
  active document, the small features that the defeature can take out,
  the triangles that it saves, the fills and caps, and the reasons for the
  features that it keeps. It also counts the open edges of the body before
  and after the plan (`open_before`, `open_after`). Both must be 0: an open
  edge is a hole in the mesh. `open_where` names the faces of the open
  edges.
- `CadderPlaneUv(options)`: for each planar face, whether the texture
  coordinates can be made again from its surface, within one micron. A
  fill of the defeature needs that, or a textured part shifts.
- `CadderTessUv(face, limit)`: the triangles of one face of the active
  part with the texture coordinates that SolidWorks gives them.

The closure count is the old `Sw.ClosureCheck` of the Bridge. It builds
the faces as the export does: the removed faces out, the planar fills
(`PlaneRefill`) and the caps (`SurfaceCap`) in. When the export changes
how it builds the faces, change `CadderClosure` in the code file too.

## Options

| Option | Default | Use |
| --- | --- | --- |
| `MaxExtentM` | 0.012 | The widest small feature, in metres. |
| `Curved` | false | Also take curved features. |
| `Quality` | the setting | The 0 to 1 mesh dial. |
| `ChordM`, `AngleRad` | | A chord tolerance in metres and an angle in radians, in place of the dial. |

## Steps

1. A lab session with the part or assembly open and active (see `cadder-bridge`).
2. Use `run` with recipe `cadder-geometry`, or give code:

```
return CadderSmallFeatures(new CadderGeometryOptions { MaxExtentM = 0.008, Curved = true });
return CadderPlaneUv();
return CadderTessUv(3, 60);
```
