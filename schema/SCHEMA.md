# Rig manifest: semantics

The manifest (`<name>.rig.json`) is the contract between the SolidWorks add-in
(`Peak.Cadder`, MIT) and the Blender add-on (`CADder`, GPL-3.0-or-later).
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
by the occurrence matcher: the Blender side matches on these plus transforms,
never on Blender object names.

**A component can BE a subassembly occurrence.** A rigid subassembly is one
body, so the walk names the assembly occurrence and stops; the parts inside it
are not components at all. Such an occurrence carries no geometry of its own in
the STEP file (it is a node with children), so a consumer must be prepared to
attach the whole subtree beneath it rather than a single shape. Components also
NEST: a flexible subassembly inside a rigid one is its own component, deeper in
the same path, and usually in a different rigid group, so the inner one owns
its parts and the outer one owns what is left.

## Rigid groups

Components with zero relative DOF merge into one group; a group maps to one
bone.

**Nothing is classified by what a part is called.** No file name, no
description, no part-library flag ever decides whether something is a joint:
only kinematics do. A bolt that can spin gets a revolute, because that is what
SolidWorks says it does; a bolt whose mates leave it no freedom joins its
neighbour, for the same reason. (Until 2026-08-24 a filter here folded a part
into its neighbour when the file name matched screw/bolt/washer/nut/pin/
dowel/rivet. It changed kinematics from metadata, and "spindle" and "pinion"
both contain "pin".)

**The status is read with the limit mates out.** SolidWorks counts a
limit mate as a fixed dimension, so a part behind one reads fully defined
while it moves. Live ClampRig (2026-08-24): the cutting head sits behind
the lead screw's limit mate, reads fully defined, and slides half a metre.
An exporter that welded on that reading took the whole lead screw assembly
and cutting head out of the rig.

So the exporter takes every limit mate out, at the top level and inside
each flexible subassembly, rebuilds, and reads the status again. Live checks
(2026-09-21) show that this reading follows the motion: the cutting head
and the hydraulic pistons read under-defined with their limits out, and the
parts that cannot move read fully defined. A top-level component that reads
fully defined then joins the ground before any mate is read. Coupling mates
stay in: a gear reads under-defined with its gear mate in.

Inside a flexible subassembly the status in the top solve does not follow
the motion: a hinge leaf reads fully defined while it swings. So each child
is read again in its subassembly's own document, where it is top level. A
child that reads fully defined there joins the subassembly's frame, because
mates in a parent can only add to what holds it. An under-defined reading
there says nothing about the parent, so the mates decide (live CutterRig,
2026-09-22: every part of the cutting head reads fully defined in its own
document, and its nuts and washers slid in Blender until this reading).

The status is also checked the other way: a top-level component that reads
under-defined with the limits out, but sits in the ground group, is logged,
because a degree of freedom was lost.

The same caution applies to the DOF probe. `GetRemainingDOFs` answers the
same question (freedom of its own), so a "no relative freedom" verdict on a
pair with a third body attached may describe a weld or a follower. Which one
depends on what the probe said about that third body, so the verdicts are
read as a SET: union them into closures, and believe a verdict only when
every body the child is mated to lies inside the child's own closure. Live
ClampRig (2026-08-24) has both shapes side by side: nine M16 bolts
each mated to the machine body AND to the buoyancy module they hold on (one
body, every pair read fixed), and a cutting head mated to the machine body
AND to the lead screw rod (a follower, and the rod link was never read
fixed).

**A flexible subassembly node grounds nothing.** SolidWorks dissolves a
flexible sub and solves its children against the top assembly, which is why
the exporter descends into it, so the node's Fix/Float flag describes a body
the solver no longer has. Live ClampRig (2026-08-24) reported both
hydraulic rams (flexible) as fixed while both clamps (rigid) were not, and
each ram barrel went into ground through its node, taking the bore pivot with
it: the rods extended without the ram swinging. The node still merges with
whatever is fixed INSIDE it: the sub is rigid with its own internal ground,
and that body is grounded only if its own mates ground it. The one exception
is an assembly whose only fixed component is such a node: it grounds then,
because the alternative is a rig of nothing but islands.

**There is exactly one grounded group.** `grounded: true` marks it, and it is
always `g000`: the armature root. Every fixed component lands in it, whether
or not a mate happens to span them: two things fixed to the assembly have no
freedom between them. A manifest with two grounded groups is malformed, and a
consumer will hit it as a joint whose CHILD is grounded, which no bone
hierarchy can root (live ClampRig, 2026-08-24: eighteen separately fixed
hose routes, and Blender refused the file).

A group with an EMPTY `components` list is a *carrier link*: a virtual body
the exporter synthesizes when one contact's residual motion does not fit a
single joint (a tangent mate, a cylinder rolling on a plane keeps two slides
and two independent spins; rim-tangent discs orbit and spin). The contact is
split into two primitive joints chained through the carrier. Consumers give
carrier groups a bone like any other group; there is simply no geometry to
parent to it.

## Joints

A joint is the *residual freedom* between two rigid groups, not a mate.

**Where each half of the answer comes from.** The mates decide the
TOPOLOGY (which body hangs off which) and nothing else can, because
SolidWorks always answers "how much freedom does this have" relative to
ground, never relative to a parent you have yet to choose. Ask about an
excavator bucket and you get the accumulated freedom of boom, stick and
bucket; the clean answer "revolute about the bucket pin, relative to the
stick" only exists once the stick has been named its parent. The SOLVER
then decides what each connection is: the probe pins the parent chain and
reads the child's remaining freedom, which is a fact about the solved
assembly rather than an inference from one pair's mate geometry. Where
both name a primitive and they differ, the solver wins.

Three things stay with the mate analysis, because the probe cannot see
them: **limits** (it has to suppress limit mates to read any freedom at
all), **couplings** (a screw reads as a plain cylindrical once its
rot–slide link is invisible, and a gear pair as two loose revolutes), and
**sampled geometry** (a path curve, a surface patch). A joint carrying a
coupling is never overridden. When a type is adopted, a limit measured
about a different line, or about a freedom the new type does not have:
is dropped and the note says so, rather than leaving a number in the file
that is about nothing.

The probe is also blind past a mate it does not neutralise: a path, a
free slot, a coupling, and SolidWorks counts several of those as
constraints. Its verdict on such a pair is advisory only: it never merges
and never overrides. Pairs inside a flexible subassembly cannot be probed
at all (in-sub limit mates are not suppressible through top-context
handles), so there the mate analysis stands alone. `axis`
is the DOF direction for revolute/prismatic/cylindrical/screw; the plane normal
for planar; null for free and for an unlimited ball. `origin` is a point on the
axis (slid to a sensible spot near the child's geometry). For prismatic and
planar joints the origin is kinematically arbitrary, so the exporter anchors it
at the child group's reference component origin: the consumer's bone lands
where the part is, not where the mated faces happen to touch. `secondary_axis`
exists only so the consumer can build a deterministic frame (Blender: bone +Y =
axis, +Z from the orthogonalised secondary axis). It carries no kinematic
meaning, **except on `pin_slot`**, where it is the slide direction, and **on a
limited `ball`**, where the pair is the swing-cone frame (both below).

**A limited ball is a swing cone.** When a ball joint carries a rotation
limit, `axis` is the limit mate's parent-side measured direction: the cone
axis, fixed in the parent group, and `secondary_axis` is the child-side
measured direction (for a ball stud, the stud's own axis), fixed in the child
group, both in the global frame at export. The limit values are the UNSIGNED
angle band: angle(`secondary_axis` transported by the child, `axis`
transported by the parent) must stay within [`min`, `max`]. `value_at_rest`
is that angle at export. Neither vector is canonicalized: the axis sign says
which way the cone opens, and the delta rule below does NOT apply: the band
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
different poses therefore always carry the same axis: a consumer's bone
never flips (live corpus 01, 2026-08-23: the earlier orient-for-the-limits
rule flipped the hinge bone with the export pose).

**Limits are signed displacements about/along the axis**: `min`, `max` and
`value_at_rest` are measured positive with right-handed rotation about
`axis` (angle limits) or translation along it (distance limits). When the
underlying mate dimension grows the other way, the exporter mirrors the
values (`−max, −min, −rest`), not the axis: same physical range, so the
values need not match the numbers SolidWorks displays. Consumers work in
deltas from the rest pose: `[min − value_at_rest, max − value_at_rest]`,
applied about `axis` with no sign question. When the rest pose is degenerate
(an angle mate resting at 0°/180°, touching distance faces) and neither the
flexed-instance geometry nor the live sign probe could resolve the sense,
the values ship as read: mirrored first when the mate's "Flip dimension"
tick is set, since the tick is exactly the other branch of that guess (live
corpus 01 hinge5, 2026-08-23; at any readable pose the entities themselves
sit on the flipped side and the geometric sense already includes it), and
the joint is marked `confidence: "medium"` with a note, at worst the limits
act mirrored, never the bone.

`type: "path"` is one slide along an arbitrary curve, a SolidWorks path mate,
or a vertex made coincident with an edge or sketch curve, which is the same
motion under another name. The curve travels in the joint's `path` object:
`points` (ordered polyline, global metres, sampled by the exporter.
SolidWorks exposes no feature data for path mates, so the curve is dug out of
the mate entities' underlying edges/sketch segments) and `closed`. `origin` is
the follower's rest position on the curve and `axis` the tangent there, so a
consumer that cannot follow curves can degrade the joint to the prismatic it
locally is. The path mate's pitch/yaw/roll orientation options are not
readable through the API and are not modelled: orientation stays free.

`type: "surface"` is a point held on an arbitrary face: two translations
across it, all three rotations free. It is the fallback for surfaces no other
joint describes (a torus, a fillet, a loft, any B-surface), which SolidWorks
mates a point to as readily as to a plane. The face travels TRIANGULATED in
the joint's `surface` object: `points` (global metres) and `triangles`
(index triples). `origin` is the contact point and `axis` the surface normal
there, so a consumer that cannot follow meshes can degrade the joint to the
planar contact it locally is. Analytic surfaces never take this route: a
plane, cylinder, cone or sphere keeps its exact joint, and only what is left
pays the cost of a mesh.

Both geometry-carrying types are sampled, so their `points` approximate the
real shape to the exporter's tolerance, but they are guaranteed to pass
exactly through `origin`, which is what the rest pose depends on.

The Blender consumer implements both the same way: a hidden mesh, a hairline
ribbon along the curve, or the face's own triangles, parented to the parent
group's bone, and a nearest-surface Shrinkwrap holding the child's bone to it.
Nearest-point is the whole trick: it makes the rest pose a fixed point of the
constraint, so the part loads where SolidWorks had it and still slides when
dragged.

`type: "free"` marks an under-mated pair (≥3 DOF, no recognised pattern). It is
exported, warned about, and left unparented rather than silently fixed.

`confidence` is `high` when the mate analysis and the solver agree, or when
the solver's verdict was adopted. It is `low` only when they disagree and
the verdict could NOT be adopted: a coupling on the joint, a mate the probe
cannot see past, or a type outside its vocabulary, in which case the mate
analysis is exported and the disagreement is a warning. `medium` is reserved for a classification the exporter had to
guess at: today only a limit whose sense the rest pose could not resolve.

## Couplings

Coupled motion is an annotation on the *driven* joint. `gear` and
`linear_coupler` reference a `driver_joint`; `rack_pinion` converts the
driver's rotation to this joint's translation via `meters_per_radian`; `screw`
is self-coupled (this joint's own translation and rotation are linked by
`lead_m_per_rev`).

`table` is a relation with no formula: `samples` lists the driven joint's
value as a function of the driver's, `[[x, y], ...]` with `x` ascending, both
relative to the exported pose and in the joints' own units (radians for a
turn, metres for a slide). A cam profile is arbitrary geometry, and a
universal joint's output is `atan2(sin x, cos x cos b)` only once the yokes'
phase is known, which no mate records. The SolidWorks solver knows both, so
the exporter turns the driving joint through one revolution in steps, reads
the driven joint at each, and writes what it read. `periodic` says the
relation repeats every `period` of `x` (a full turn of the driver); a table
that is not periodic clamps at its ends. A consumer maps the driver's channel
through the samples with linear interpolation between them. In Blender that
is a driver F-curve with the samples as its keyframes and a cycles modifier
when periodic: the driver's value is the curve's input, and the bone never
sees the difference from a formula. Written for cam-follower mates and
universal-joint mates; before it, the cam relation was left to the user's
hand and the universal joint was a 1:1 gear.

`cam` is the other cam route, for a cam the exporter cannot turn through a
table: a cam free in its plane, a cam on a slide, or the probe switched off.
The cam path's faces travel triangulated in `cam.surface` (global metres at
the exported pose), with the cam's joint axis and a point on it, and the
follower's contact entity in `cam.follower`: a `vertex`, a `roller` (a
cylinder or a ball, with its `radius`), or a `flat` face (with its `normal`).
The consumer holds the follower on the faces itself, whatever the cam does.
A vertex or a roller projects along the follower's slide onto the faces (the
roller onto a copy pushed out by its radius). A flat face is the support
function of the faces along its normal, which separates into a table in the
cam's rotation plus a linear term in its translation, both relative to the
follower's base. Only a sliding follower (a `prismatic` joint) carries it.

`mirror` reflects the driven joint's body across `mirror_plane`
(`{point, normal}`, global metres/unit). How MUCH of the pose is reflected
is `mirror_scope`, and that is the whole subtlety. It takes two values;
absent means `"plane"`, which is what the field's introduction changed
nothing about.

`"plane"`: a symmetric MATE between two planar faces. That is a
plane-to-plane relation, so it removes THREE freedoms: the translation
along the plane normal and the two rotations that tilt it. The other
three (sliding within the plane and spinning about its normal), are left
free, and the two bodies do them INDEPENDENTLY. One block can be raised
without the other; SolidWorks allows exactly that (live corpus 14 sym4,
2026-08-24, after an earlier full 6-DOF reading welded the pair into one
rigid mirror image).

`"rigid"`: an assembly MIRROR FEATURE. There the instance is a full
reflection of its source, position and orientation both, so all six
follow and the pair really is one rigid mirror image. A mirror feature
declares no mate, so nothing in the pairwise mate graph records the
relation; the exporter reads the feature for its source components and
then pairs them by geometry, which also answers the question a lookup
cannot: whether the instance is STILL a reflection. One that has since
been dragged is a glide reflection, and it gets no coupling. Whether the
feature built an opposite-hand part or merely repositioned the original
makes no difference: the manifest carries kinematics, and the geometry
comes from the model either way.

Consumers get this almost for free if they rest both bones with the same
plane-aligned orientation (local +Y = the normal): the reflection then
reduces to per-channel sign flips, and the three channels that NEGATE
(loc y, euler x, euler z) are precisely the three the relation
constrains. Driving those and leaving the rest alone is the whole
implementation.

The exporter synthesizes the coupling for a symmetric mate between two
bodies whose WHOLE relation is that mate: no other joints touch either
body. Both bodies get ground-rooted `free` joints, and consumers treat
those two free joints as ordinary tree edges. Only PLANAR mirrored
entities are modelled: a symmetric mate on points or axes constrains a
different set of freedoms, and is warned about rather than read as if it
were planar. A symmetric mate whose bodies carry their own revolute or
prismatic mounts still collapses to the one-number `gear`/`linear_coupler`
as before.

## Loops

The joint graph may contain cycles; bone hierarchies cannot. The exporter runs
a spanning tree from the grounded group and reports every non-tree edge as a
loop with a chosen `closure_joint` (the cut). The consumer parents along the
tree and re-closes each loop at the cut. Consumers must not re-derive loops.

`closure_kind` says HOW to re-close it. Absent means `"ik"`, which is what
every manifest written before the field meant.

`mobility` says how many inputs the loop takes: its members' freedom less the
three that closing a planar ring spends. It is 1 for almost every loop, and 1
for every manifest written before the field. A four-bar is 1. A five-bar (four
pins and a screw around one ring, which is what an adjustable wrench is) is 2.

A loop of mobility `m` must leave `m-1` bones of the driven chain OUT of the
solve, taken from the ROOT end, where they move the most. Those bones are
controls: the user poses them beside the loop's own driver, and the solver
closes the ring around whatever they do. Solve the whole chain instead and the
solver spends a freedom the user is meant to hold, then fights whatever the
user does with it.

The exporter counts this only where each member's part in it is plain: a
PLANAR ring built from pins about its normal and slides in its plane.
Everything else is 1. Under-counting costs a control the user could have had;
over-counting takes a constraint away, and that is the error that shows as a
mechanism coming apart.

**`"ik"`**: the cut is a pin, and the closure is a point coincidence: the
consumer solves the driven side so the two halves of the cut meet again. The
cut is chosen to be a joint whose bodies SHARE a point (revolute, cylindrical,
ball); a planar or prismatic cut would ask the consumer to pin two bodies at a
point that slides.

**Finding the mirror of a symmetric mate.** A three-body symmetric mate
arrives as three plane entities, and which one is the mirror is not labelled.
It is the plane that REFLECTS the other two onto each other: not the one
parallel to and midway between them. The mirrored entities ride the moving
bodies, so they are parallel to the mirror only at the mechanism's symmetric
zero, and a saved assembly sits wherever it was left. The reflection is
compared as plane EQUATIONS (unit normal and signed offset, the normal's sign
allowed to flip), never as the entities' points: those are wherever SolidWorks
named them and differ between two faces of one plane.

**`"aim_pair"`**: the loop is a **slider-crank**: exactly one member is a
slide, and the cut IS that slide. No rotational solver can lengthen a sliding
joint, so leaving it inside a solved chain freezes the mechanism, and cutting a
pin instead just locks the slide inside the tree with the same result. Cut at
the slide and each half hangs off its own pin, free to aim at the other, which
is how a hydraulic ram is rigged by hand. The consumer points each half at the
other's pivot. Aiming them straight at each other is a dependency cycle, so
each aims at a duplicate of the other's pivot carried on the other's PARENT;
those parents are the posed input and the ground, and neither is aimed at
anything.

**Both mounts of an aim pair are SEATED on the slide.** A pin's origin is only
defined up to sliding along its own axis: every point of the pin line is the
same joint, and which one arrives is whichever entity the mate happened to
name: the top of a lug, the centre of one circular edge. The aim closure
cannot live with that, because it stands in for the slide by pointing each
half at the other's pivot, and that reproduces the slide only when both pivots
lie on the slide's own axis. Off it, the halves aim ACROSS the ram instead of
along it. So the exporter slides each mount along its own axis to the point
closest to the slide's axis line, which is free (same axis, same twist, same
joint) and is where the pin really crosses the ram. A pin PARALLEL to the
slide has no such point and is left alone with a note; a pin that still misses
the slide's axis after seating is seated as far as it can be, and the miss is
reported in the joint's `notes`.

(Live ClampRig, 2026-08-24: the bore pin arrived 45 mm above the ram
axis and the rod pin 31 mm below it, so the line between the two bones ran
6.19° off the ram and the rod met the bore at an angle. Both pin lines cross
the ram axis exactly at the ram's own centre plane; seated, the rest error is
0.000°.)

**A pinned half is a LOCKED track, not a damped one.** A body on a pin can
turn about that pin and nothing else. Blender's Damped Track aims one axis and
leaves every other rotation free, so the ram may roll along its own length and
swing out of the plane its pin allows; a Locked Track about the pin says
exactly what the mate says. The consumer therefore rests these bones with
local Z ON the pin (local +Y is the aim direction PROJECTED into the plane the
pin turns in, so the two agree exactly wherever the pin stands square to the
ram) and locks that axis. A mount with no axis of its own: a ball, a free
pair, keeps the Damped Track, because nothing is known to lock. The bone's
own rotation channels are all locked either way: the closure owns the
orientation, and a rotation limit on such a mount would clamp the wrong axis
(local Y is the ram, not the pin). Nothing is lost with it: that pin is
stopped by the ram's own stroke at the far end of the loop.

**`"none"`**: the consumer must not solve this cut at all. The loop is still
validated like any other; only the solve is skipped. Two shapes arrive this
way.

BOTH of the loop's edges to the anchor are slides: two bodies each sliding on
the same ground, tied to each other. Cutting between them leaves them siblings
and nothing can then make one carry the other, because a rotational solve has
nothing to rotate. Cutting one of the anchor edges instead turns the loop into
a chain: pose the driver and the far body simply comes along, so the TREE
already carries the motion. (Live ClampRig: the cutting head and the
lead screw rod both slide along the machine with the head mated to the rod.
Cut between them, and driving the lead screw left the cutting head behind.)

A cut nobody solves would take its constraint with it, and SolidWorks does not
lose it, so the exporter puts it back on the ring's TREE joints, as freedom
removed. What the rest of the ring cannot reproduce, the ring forbids: each
member contributes its screw twists (a revolute about the line `(a, p)` is
`(a, p×a)`, a slide along `d` is `(0, d)`), and a freedom of the joint under
test survives only if its own twist lies in the span of the others'. The
dimension of that intersection decides how much is removed before anything is
renamed, so a joint pinned down to a coupled motion none of its own axes
performs (a cylindrical reduced to a screw), is exported unchanged with a note
rather than wrongly welded. Nothing is ever made freer this way.

The transfer REFUSES in four cases, each of which would otherwise weld a
mechanism shut: far worse than leaving a freedom in. A joint carrying a
coupling, because that mate drives one of its freedoms and this reading cannot
see it. A screw, because its turn and slide are one coupled motion whose pitch
the manifest does not carry. A survivor that is a coupled motion none of the
joint's own axes performs alone, which the schema cannot name. And a DEAD
CENTRE: the span test is a first-order statement, so at the end of a stroke or
over centre a joint that travels finitely has zero rate for every admissible
velocity and reads as held. Each removal is re-asked with the ring's origins
nudged and its axes untouched: a real hold is in how the axes are ORIENTED,
which the mates fix; a dead centre is in where the parts happen to SIT, and a
freedom that comes back was never held. Every refusal is written to the
joint's `notes` and drops its `confidence` to `medium`.

(Live ClampRig: both hydraulic rams are pinned to the machine by a
concentric. One is held along its pin by a width mate; the other is held only
by a coincident between the two rams' front planes: it is located THROUGH the
first ram. Read pairwise its joint is a cylindrical, free to slide on its pin;
the mate that stops it is the one on the cut.)

Or the cut SHARES NO POINT. An IK closure re-joins a cut by making one point
meet again, so a planar, prismatic or contact cut is one the consumer cannot
honour: solving it would drag a body to pin a face against a face, and that
body is usually carrying something else. (Live ClampRig: the two
hydraulic rams are held level by a coincidence between their subassembly
mid-planes, which reads as a planar joint between the two barrels. Cut there
and IK-closed, it pulled one barrel off the Damped Track aiming it at its own
rod, and that ram stopped following its clamp.) The exporter prefers a cut
that does share a point (revolute, cylindrical, ball, fixed) and reaches
this case only when the ring offers none.

The `suggested_driver_joint` of an aim pair is the loop's pin that touches
NEITHER sliding body: posing it moves the two pivots apart and the ram
follows.

**`driver_candidates`** lists every input the exporter weighed for the loop,
the chosen one first, each as `{joint, closure_joint, closure_kind}`. A
one-degree mechanism can usually be driven from more than one joint (the
crank of a slider-crank, or its slider), and which one is convenient depends
on what the rig is for, so a consumer may offer the list and let the user
choose. It must apply the whole candidate, never the joint alone: the cut is
the ring edge beside the driver's moving body, so moving the driver to the
other end of the ring moves the cut with it, or the solved chain no longer
contains the bodies that have to move. The list is informative: to switch
inputs a consumer applies a mechanism option (below), never a candidate on
its own. The kinematics never depend on any name: the candidates are joints,
and a label built from part names is for the person choosing.

**`mechanisms`** groups the loops that share joints. Such loops close one
degree of freedom and must all be driven from the same input, and which loop
is met first decides how every other loop of the mechanism is cut. So each
input a mechanism can take is exported as a COMPLETE alternative, computed by
the same choice the exporter made for its own suggestion with that input
counted as already chosen: `inputs[].loops` are the mechanism's loops under
that input (same ids, same order, re-chosen cuts, closures and members) and
`inputs[].flipped_joints` names the joints whose `parent_group` and
`child_group` swap, because the new tree reaches them from the other side.
`inputs[].joint_limits` names the joints whose limits differ under that
input, with the limits that apply then (`null` = unlimited): a stroke limit
derived onto a slider-crank's crank (see below) belongs to the crank-driven
configuration, and the slider-driven one has the slide's own limit instead. A
consumer applies them with the option and restores the joints' own limits
when it leaves it. `inputs[0]` is the exporter's choice: its loops equal the
top-level `loops`, its flipped list is empty and it changes no limit. An option's `joint` is the joint the user will
pose under it: the driver no other loop's chain solves (two loops of one
mechanism can name two drivers, and a driver whose body sits inside another
loop's solved chain is no control). The exporter's choice is the
configuration with the fewest such controls, and `loops` are written in
solving order: a loop whose driver's body another loop's chain solves comes
after that loop, and a consumer that solves loops in manifest order and cuts
a later chain short at a body an earlier chain owns gets the mechanism
right. A consumer switches inputs by replacing the
mechanism's loops with the option's and swapping the named joints, and by
undoing the previous option's swaps first. Inputs the re-choice does not take
(a slide the ram rule turns into an aim pair driven from its pin) are not
offered. Applying a per-loop candidate on its own left the other loops of a
mechanism on the old input: three controls on one degree of freedom, then a
tree the members no longer described (live plunger.sldasm, 2026-09-15).

**A slider-crank's stroke limit arrives on its driver.** Cutting at the slide
makes the driver a free input again, so a clamp worked by a ram would swing
past the point where the ram bottoms out and leave the rod behind. The two are
one constraint seen from two corners of a fixed triangle: the ram's mount, the
driver's pivot, the rod's mount, with only the ram's own length varying, so
the exporter converts one into the other exactly:

    cos(ABC) = (|AB|² + |BC|² − |AC|²) / (2·|AB|·|BC|)

and puts the resulting angle range on the driver as a rotation limit whose
`value_at_rest` is 0, because the values ARE displacements from rest: there is
no SolidWorks dimension behind that one. A stroke is a SIGNED displacement
along the slide's own axis while the ram's length is a distance, so the two
agree only when that axis runs from the ram's mount towards the rod's; on the
mirrored hand of a machine it runs the other way and a stroke that lengthens
the ram reads as a falling coordinate. The slide keeps its own limit; the two
now agree instead of one being free to break the other. A driver carrying its
own limit mate keeps it, and a non-planar loop is left alone: the closed form
is a planar one.

## Warnings

Machine-readable (`code`) + human-readable (`message`). Established codes:
`UNCLASSIFIED_PAIR`, `UNDER_DEFINED`, `LOW_CONFIDENCE`, `OCCURRENCE_UNMATCHED`,
`LIMIT_AXIS_MISMATCH`, `DISCONNECTED_ISLAND`, `SUPPRESSED_SKIPPED`,
`SYMMETRIC_COUPLING`, `MIRROR_COUPLING`, `PROBE_DISAGREES`,
`SHARED_FLEXIBLE_GEOMETRY`.
