# The direct link: .swmesh and the return leg

Two ways to get an assembly into Blender:

| | STEP | direct |
|---|---|---|
| what travels | a solid model | triangles |
| who tessellates | OpenCASCADE, in Blender | SolidWorks, in the add-in |
| quality | chosen in Blender, any time | chosen at export, per part |
| appearances | rewritten into AP214 entities | carried per triangle |
| identity | matched afterwards, by name and pose | tagged at the source |
| speed | writes and re-reads a whole solid model | tessellation only |

Neither replaces the other. STEP is the one to use when the geometry
matters more than the wait, or when the file is going somewhere that is
not Blender. The direct link is for iterating, and for the assemblies
where the STEP round trip is the reason you stop iterating.

The rig manifest is unchanged and orthogonal: both paths write it, and it
is what carries the kinematics either way. Component ids tie the two
files together.

## Why the add-in can do this at all

Every call involved is in the SolidWorks API that ships with every seat:
`IBody2.GetTessellation`, `IFace2.GetTessTriangles`, `IComponent2.GetBodies3`,
`MaterialPropertyValues`. No Document Manager, no partner programme, no
licence key. (KeyShot's discontinued SolidWorks plugin worked the same
way; its plugin folder holds no SolidWorks assemblies at all.)

One difference from how KeyShot did it: it read the DISPLAY tessellation
(`GetTessTriangles`), whose quality is whatever the document's image-quality
slider is set to, and which can only be changed by reaching into the
user's settings. `IBody2.GetTessellation` takes an explicit chord
tolerance, so quality is a per-request argument and nothing the user owns
is touched. That is what makes the return leg below possible.

## The format

Binary, little-endian throughout, written by `Core/MeshWriter.cs` and read
by `rig/swmesh.py`. Strings are a `uint16` byte count followed by UTF-8.
Arrays are contiguous and fixed-width so a consumer can read them into a
typed buffer in one go. Blender's `foreach_set` wants exactly that, and
on an assembly of any size the difference is not subtle.

```
header
  uint32   magic          'SWMH' (0x484D5753)
  uint32   version        1
  uint32   flags          1 = normals present, 2 = UVs present
  float64  tolerance      chord tolerance the scene was built at, metres
  uint32   material_count
  uint32   definition_count
  uint32   instance_count

material × material_count
  string   name
  float32  r, g, b, a
  float32  roughness, metallic
  string   texture        absolute path, or empty

definition × definition_count
  int32    id
  string   name
  uint32   vertex_count
  uint32   triangle_count
  float32  positions[3 × vertex_count]      metres, PART space
  float32  normals[3 × vertex_count]        if the normals flag is set
  float32  uvs[2 × vertex_count]            if the UV flag is set
  int32    triangles[3 × triangle_count]    indices into positions
  int32    triangle_materials[triangle_count]

instance × instance_count
  int32    definition_id
  string   component_id   the rig manifest's c001, c002, ...
  string   name
  float64  transform[16]  row-major 4×4, metres, global
```

Three choices worth the words:

**Definitions and instances are separate.** A part used two hundred times
is tessellated once and placed two hundred times, and becomes one Blender
mesh datablock with two hundred objects sharing it. On a real assembly
this is most of the speed.

**Positions are float32, transforms are float64.** A float carries about
seven significant digits, so a 10 m assembly resolves to under a micron:
finer than any tessellation would be asked for, at half the bytes. A
transform is different: fold a rotation into float32, put it through a
bone rest pose, and the drift is exactly what the rig spent three rounds
chasing out.

**Materials are per triangle.** SolidWorks resolves appearance per face,
per body, per feature and per component; the triangle is the only place
all four collapse to a single answer.

## Where the time goes

Tessellation is the whole cost, and it is COM traffic rather than
arithmetic. `ITessellation` has no bulk reader: the reflected interface
offers `GetVertexPoint(i)`, `GetFacetFins(f)`, `GetFinVertices(fin)` and
nothing that hands back an array of all of them, so reading a body costs
roughly **three cross-apartment calls per vertex and four per facet**, each
marshalling its own small SAFEARRAY. A 100k-triangle body is on the order of
a million calls.

That shapes what is worth optimising:

- **Appearance resolves once per FACE, not once per triangle.**
  `MaterialPropertyValues` is itself a COM property, and it used to be asked
  for every facet. Walking facets face-by-face through the face-facet map
  also retires `GetFacetFace`, a call and a COM object per triangle.
- **Definitions are shared**, so a part placed two hundred times is
  tessellated once. On a real assembly this is the largest single saving and
  it costs nothing.
- **Hidden components are skipped**, as hidden children of a rigid
  subassembly always were.
- **The quality dial is the biggest lever the user holds.** Tolerance enters
  the triangle count roughly quadratically: DRAFT is about six times coarser
  than BALANCED, which is something like thirty times fewer triangles.
  Iterate on DRAFT, then ask for one part again at FINE: that is what the
  return leg below is for.

**None of it is parallel, and none of it can be.** The SolidWorks API is
single-threaded and must be called on the thread SolidWorks owns; that is
the same constraint the return leg's hidden control exists to satisfy. The
arithmetic between the COM calls (stitching, winding, rebasing), is a few
adds per triangle and moving it to another thread would buy nothing worth
the complexity.

The export log names any part that took over half a second, with its
triangle count, and prints the total and the slowest part at the end.

## The return leg

The add-in also listens, so Blender can ask for something rather than only
be sent to. Discovery mirrors the bridge inside the Blender add-on: each
process writes a small JSON file naming its port and a per-session token.

```
%LOCALAPPDATA%\PeakDesign\SwToBlender\solidworks\<pid>.json
    {"pid": …, "port": …, "token": …, "addin_version": …}
```

`POST /ping` needs no token and answers `{"ok": true, …}`. Everything else
goes to `POST /job` with an `X-SWTB-Token` header and a JSON body naming an
`op`:

| op | asks for | answers with |
|---|---|---|
| `status` | what SolidWorks has open | `document`, `title` |
| `retessellate` | `components` again at `quality` (0..1) | `mesh` (a path), `triangles`, `tolerance_m` |

The reply names a **file**, not geometry. Both ends are on 127.0.0.1 by
construction, so a megabyte of triangles has no business being JSON-escaped
through a socket.

Every op is read-only. The return leg exists so a viewport can ask for
better geometry, not so a renderer can edit a CAD model behind its owner's
back.

### Threading

SolidWorks API calls must happen on the thread SolidWorks owns, and
`HttpListener` hands requests to pool threads. So a request does no work of
its own: it parks a job and blocks, the SolidWorks thread runs it through a
hidden control's `Invoke`, and the reply goes back on the pool thread that
was waiting. The Blender bridge solves the same problem with a timer pump,
for the same reason.
