# Rig manifest — semantics

The manifest (`<name>.rig.json`) is the contract between the SolidWorks add-in
(`Peak.SwToBlender`, MIT) and the Blender add-on (`sw_to_blender`, GPL-3.0-or-later).
It is written in the same pass as the STEP file so that every name in it matches
the exported file exactly. The JSON Schema in `rig-manifest.schema.json` is
normative for shape; this file is normative for meaning.

## Versioning

`manifest_version` is semver. A minor bump adds fields; consumers ignore fields
they do not know. A major bump may change meaning; consumers must refuse a major
they do not support. Both halves of the tool declare the range they support.

## Units and frames

- Length metres, angles radians. No millimetres anywhere in the file.
- All vectors, points and transforms are in the **assembly's global frame**:
  right-handed, Z-up, exactly as the SolidWorks API reports them.
- Transforms are row-major 4×4, rows `[R|t]`, bottom row `[0,0,0,1]`.
- The STEP file is exported in the same global frame. The Blender importer's
  scale factor is reconciled at match time via the importer's
  `STEP_applied_scale` property, never baked into the manifest.

## Scope (WYSIWYG)

The exporter analyses only the mates of the open top-level assembly, plus the
internal mates of subassemblies whose solving mode is FLEXIBLE (recursively).
A rigid subassembly is one component here, whatever moves inside it. What moves
in SolidWorks is what gets a joint; nothing else does.

## Components

One entry per occurrence that survives the walk (suppressed components are
listed with `suppressed: true` and belong to no rigid group). `id` is stable
within the file and is the only key other sections use. `step_name` and
`step_occurrence_path` are the exact strings written to the STEP file, produced
by the occurrence matcher — the Blender side matches on these plus transforms,
never on Blender object names.

## Rigid groups

Components with zero relative DOF merge into one group; a group maps to one
bone. `grounded: true` marks the group(s) containing fixed components — the
armature root. Fasteners fold into the group they are bolted to.

A group with an EMPTY `components` list is a *carrier link*: a virtual body
the exporter synthesizes when one contact's residual motion does not fit a
single joint (a tangent mate — a cylinder rolling on a plane keeps two slides
and two independent spins; rim-tangent discs orbit and spin). The contact is
split into two primitive joints chained through the carrier. Consumers give
carrier groups a bone like any other group; there is simply no geometry to
parent to it.

## Joints

A joint is the *residual freedom* between two rigid groups, not a mate. `axis`
is the DOF direction for revolute/prismatic/cylindrical/screw; the plane normal
for planar; null for free and for an unlimited ball. `origin` is a point on the
axis (slid to a sensible spot near the child's geometry). For prismatic and
planar joints the origin is kinematically arbitrary, so the exporter anchors it
at the child group's reference component origin — the consumer's bone lands
where the part is, not where the mated faces happen to touch. `secondary_axis`
exists only so the consumer can build a deterministic frame (Blender: bone +Y =
axis, +Z from the orthogonalised secondary axis) — it carries no kinematic
meaning, **except on `pin_slot`**, where it is the slide direction, and **on a
limited `ball`**, where the pair is the swing-cone frame (both below).

**A limited ball is a swing cone.** When a ball joint carries a rotation
limit, `axis` is the limit mate's parent-side measured direction — the cone
axis, fixed in the parent group — and `secondary_axis` is the child-side
measured direction (for a ball stud, the stud's own axis), fixed in the child
group, both in the global frame at export. The limit values are the UNSIGNED
angle band: angle(`secondary_axis` transported by the child, `axis`
transported by the parent) must stay within [`min`, `max`]. `value_at_rest`
is that angle at export. Neither vector is canonicalized — the axis sign says
which way the cone opens — and the delta rule below does NOT apply: the band
is absolute about the parent-fixed axis, never about the child's rest pose
(live corpus 04, 2026-08-23: a rest-relative cone tilted with the export
pose, and per-axis Euler limits let ~1.27× the limit through at diagonal
azimuths). Twist about the child direction is unconstrained.

`type: "pin_slot"` is a pin sliding in a slot: one rotation about `axis` plus
one translation along `secondary_axis`, the two mutually perpendicular. It is
what a face-coincident plus a width mate on a cylindrical tab leaves (a puck
centred on a plate by one width: it slides along the slot and spins about its
own axis). `origin` sits on the rotation axis in the rest pose. Rotation
limits apply about `axis`, translation limits along `secondary_axis`; the two
DOFs carry their limit senses independently in their values (below).

**The axis sign is canonical.** The mate geometry fixes the axis LINE; the
sign along it is a pure function of that line (a fixed weighted sum of the
components must come out positive), NEVER of entity order, mate flip state,
or the pose the model was exported at. Two exports of the same assembly in
different poses therefore always carry the same axis — a consumer's bone
never flips (live corpus 01, 2026-08-23: the earlier orient-for-the-limits
rule flipped the hinge bone with the export pose).

**Limits are signed displacements about/along the axis** — `min`, `max` and
`value_at_rest` are measured positive with right-handed rotation about
`axis` (angle limits) or translation along it (distance limits). When the
underlying mate dimension grows the other way, the exporter mirrors the
values (`−max, −min, −rest`), not the axis — same physical range, so the
values need not match the numbers SolidWorks displays. Consumers work in
deltas from the rest pose: `[min − value_at_rest, max − value_at_rest]`,
applied about `axis` with no sign question. When the rest pose is degenerate
(an angle mate resting at 0°/180°, touching distance faces) and neither the
flexed-instance geometry nor the live sign probe could resolve the sense,
the values ship as read — mirrored first when the mate's "Flip dimension"
tick is set, since the tick is exactly the other branch of that guess (live
corpus 01 hinge5, 2026-08-23; at any readable pose the entities themselves
sit on the flipped side and the geometric sense already includes it) — and
the joint is marked `confidence: "medium"` with a note — at worst the limits
act mirrored, never the bone.

`type: "path"` is one slide along an arbitrary curve — a SolidWorks path mate,
or a vertex made coincident with an edge or sketch curve, which is the same
motion under another name. The curve travels in the joint's `path` object:
`points` (ordered polyline, global metres, sampled by the exporter —
SolidWorks exposes no feature data for path mates, so the curve is dug out of
the mate entities' underlying edges/sketch segments) and `closed`. `origin` is
the follower's rest position on the curve and `axis` the tangent there, so a
consumer that cannot follow curves can degrade the joint to the prismatic it
locally is. The path mate's pitch/yaw/roll orientation options are not
readable through the API and are not modelled — orientation stays free.

`type: "surface"` is a point held on an arbitrary face: two translations
across it, all three rotations free. It is the fallback for surfaces no other
joint describes — a torus, a fillet, a loft, any B-surface — which SolidWorks
mates a point to as readily as to a plane. The face travels TRIANGULATED in
the joint's `surface` object: `points` (global metres) and `triangles`
(index triples). `origin` is the contact point and `axis` the surface normal
there, so a consumer that cannot follow meshes can degrade the joint to the
planar contact it locally is. Analytic surfaces never take this route: a
plane, cylinder, cone or sphere keeps its exact joint, and only what is left
pays the cost of a mesh.

Both geometry-carrying types are sampled, so their `points` approximate the
real shape to the exporter's tolerance — but they are guaranteed to pass
exactly through `origin`, which is what the rest pose depends on.

The Blender consumer implements both the same way: a hidden mesh — a hairline
ribbon along the curve, or the face's own triangles — parented to the parent
group's bone, and a nearest-surface Shrinkwrap holding the child's bone to it.
Nearest-point is the whole trick: it makes the rest pose a fixed point of the
constraint, so the part loads where SolidWorks had it and still slides when
dragged.

`type: "free"` marks an under-mated pair (≥3 DOF, no recognised pattern). It is
exported, warned about, and left unparented rather than silently fixed.

`confidence` is `high` when the mate-table classification and the DOF probe
agree, `low` when they disagree (the table's verdict is kept, the disagreement
is a warning), `medium` for heuristic classifications (e.g. fastener filter
overrides).

## Couplings

Coupled motion is an annotation on the *driven* joint. `gear` and
`linear_coupler` reference a `driver_joint`; `rack_pinion` converts the
driver's rotation to this joint's translation via `meters_per_radian`; `screw`
is self-coupled (this joint's own translation and rotation are linked by
`lead_m_per_rev`).

`mirror` is the full 6-DOF reflection: the driven joint's body poses as the
exact mirror image of the driver joint's body across `mirror_plane`
(`{point, normal}`, global metres/unit). The exporter synthesizes it for a
symmetric mate between two bodies whose WHOLE relation is that mate (no
other joints touch either body — live corpus 14 sym4, 2026-08-23): both
bodies get ground-rooted `free` joints, and consumers treat those two free
joints as ordinary tree edges — the driver body free to pose, the driven
body entirely driver-owned. (The Blender consumer rests both bones with the
same plane-aligned orientation, which reduces the reflection to per-channel
sign flips.) A symmetric mate whose bodies carry their own revolute or
prismatic mounts still collapses to the one-number `gear`/`linear_coupler`
as before.

## Loops

The joint graph may contain cycles; bone hierarchies cannot. The exporter runs
a spanning tree from the grounded group and reports every non-tree edge as a
loop with a chosen `closure_joint` (the cut — preferentially a revolute). The
consumer parents along the tree and closes each loop at the cut with a solver
(IK targeting a helper on the driver side). Consumers must not re-derive loops.

## Warnings

Machine-readable (`code`) + human-readable (`message`). Established codes:
`UNCLASSIFIED_PAIR`, `UNDER_DEFINED`, `LOW_CONFIDENCE`, `OCCURRENCE_UNMATCHED`,
`LIMIT_AXIS_MISMATCH`, `DISCONNECTED_ISLAND`, `SUPPRESSED_SKIPPED`.
