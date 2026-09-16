using System.Collections.Generic;

namespace Peak.Cadder.Core.Model
{
    /// <summary>
    /// The manifest DOM. This namespace has no dependency on
    /// SolidWorks.Interop.*: the Sw\ layer fills it, ManifestWriter emits it,
    /// and the unit tests build it directly. Shape and meaning are fixed by
    /// schema\rig-manifest.schema.json and schema\SCHEMA.md; this file follows
    /// them, never the other way round.
    /// </summary>
    public sealed class RigManifest
    {
        public string ManifestVersion = "1.0.0";
        public GeneratorInfo Generator = new GeneratorInfo();
        public StepExportInfo StepExport = new StepExportInfo();
        public List<ManifestComponent> Components = new List<ManifestComponent>();
        public List<RigidGroup> RigidGroups = new List<RigidGroup>();
        public List<RigJoint> Joints = new List<RigJoint>();
        public List<RigLoop> Loops = new List<RigLoop>();
        public List<RigMechanism> Mechanisms = new List<RigMechanism>();
        public List<ManifestWarning> Warnings = new List<ManifestWarning>();
    }

    public sealed class GeneratorInfo
    {
        public string Name = "Peak.Cadder";
        public string Version = "0.0.0";
        public string SolidWorksVersion;
        public string ExportedUtc;
    }

    public sealed class StepExportInfo
    {
        public string File;
        public string Ap = "AP214";
        public string Sha1;
        public string OccurrenceMatching;
    }

    public sealed class ManifestComponent
    {
        public string Id;                    // c001, c002, ... unique per occurrence
        public string SwPath;                // Gripper/Jaw-1
        public string SwPersistentId;
        public string StepName;              // exact PRODUCT name in the STEP
        public string StepOccurrencePath;    // null when matching failed
        public double[,] Transform;          // 4x4 row-major, global, metres
        public double[] BboxMin;             // null when unavailable
        public double[] BboxMax;
        public bool Suppressed;
        public string SubassemblySolving;    // "rigid" | "flexible" | null
    }

    public sealed class RigidGroup
    {
        public string Id;                    // g000 = grounded by convention
        public string Name;                  // bone-name slug; Id stays authoritative
        public List<string> Components = new List<string>();
        public bool Grounded;
        public double[,] Frame;              // null allowed
        public double? BboxDiag;             // metres; bone-length heuristic
    }

    public static class JointType
    {
        public const string Fixed = "fixed";
        public const string Revolute = "revolute";
        public const string Prismatic = "prismatic";
        public const string Cylindrical = "cylindrical";
        public const string Ball = "ball";
        public const string Planar = "planar";

        /// <summary>Pin in a slot: one rotation about `axis` plus one
        /// translation along `secondary_axis` (perpendicular to it). The only
        /// joint whose secondary axis is semantic, not just bone roll.</summary>
        public const string PinSlot = "pin_slot";
        public const string Screw = "screw";

        /// <summary>One slide along an arbitrary sampled curve (a path
        /// mate). `axis` is the curve tangent at the rest position; the
        /// curve itself travels in RigJoint.PathPoints.</summary>
        public const string Path = "path";

        /// <summary>A point held on an arbitrary face: two translations
        /// across the surface and all three rotations. `axis` is the surface
        /// normal at the rest position and `origin` the contact point; the
        /// face itself travels triangulated in RigJoint.SurfacePoints. The
        /// fallback for surfaces no analytic joint describes: a torus, a
        /// fillet, a loft, which SolidWorks mates to as readily as a
        /// plane.</summary>
        public const string Surface = "surface";
        public const string Free = "free";
    }

    public sealed class RigJoint
    {
        public string Id;                    // j001, j002, ...
        public string Type;                  // one of JointType.*
        public string ParentGroup;
        public string ChildGroup;
        public double[] Origin;              // null for free
        public double[] Axis;                // unit, global; null for ball/free
        public double[] SecondaryAxis;       // never null when Axis is set
        public JointLimit RotationLimit;     // null = unlimited/continuous
        public JointLimit TranslationLimit;
        public JointCoupling Coupling;
        public List<SourceMate> SourceMates = new List<SourceMate>();
        public string Confidence = "high";   // high | medium | low
        public string Notes;

        /// <summary>Path joints only: the sampled curve the child follows,
        /// global metres, ordered along the path. Null for every other
        /// type.</summary>
        public double[][] PathPoints;
        public bool PathClosed;

        /// <summary>Surface joints only: the face the child's point stays
        /// on, triangulated in global metres. Triangles index into
        /// SurfacePoints. Null for every other type.</summary>
        public double[][] SurfacePoints;
        public int[][] SurfaceTriangles;

        /// <summary>Free joints only, never serialised: the residual rotation
        /// freedom the motion resolver left for the pair. LoopAnalyzer reads
        /// it to turn a rotation-locking mate that forms no joint (a parallel
        /// mate between two moving links) into a gear coupling on the tree
        /// joints it actually constrains (live corpus 06 parallelogram3,
        /// 2026-08-22).</summary>
        public bool ResidualKnown;
        public RotFreedom ResidualRot;
        public double[] ResidualRotDir;      // AboutLine / AboutDirection only
    }

    /// <summary>
    /// Absolute mate values plus the value in the exported configuration.
    /// Consumers use deltas from rest: [Min − ValueAtRest, Max − ValueAtRest].
    /// </summary>
    public sealed class JointLimit
    {
        public double Min;
        public double Max;
        public double ValueAtRest;
    }

    public sealed class JointCoupling
    {
        public string Kind;                  // gear | rack_pinion | screw | linear_coupler | mirror | table | cam
        public string DriverJoint;           // null for screw self-coupling

        /// <summary>table only: the driven joint's value as a sampled
        /// function of the driver's, [[x, y], ...] with x ascending, both
        /// in the joints' own units (radians for a turn, metres for a
        /// slide), relative to the exported pose. Periodic tables repeat
        /// every Period of x. Read off the live model by RelationProbe,
        /// which is how a cam profile or a universal joint's fluctuation
        /// reaches the consumer without a formula.</summary>
        public double[][] Samples;
        public bool Periodic;
        public double Period;
        public double? Ratio;                // gear (rad/rad, negative = reversal), linear_coupler (m/m)
        public double? MetersPerRadian;      // rack_pinion
        public double? LeadMPerRev;          // screw

        /// <summary>Mirror couplings only: the driven joint's body poses as
        /// the exact mirror image of the driver's body across this plane
        /// (live corpus 14 sym4, 2026-08-23, a symmetric mate between two
        /// otherwise unmated bodies).</summary>
        public double[] MirrorPlanePoint;
        public double[] MirrorPlaneNormal;   // unit, global

        /// <summary>Mirror couplings only: HOW MUCH of the pose is mirrored.
        /// "plane" is a symmetric MATE between two planar faces: a
        /// plane-to-plane relation, so only the translation along the normal
        /// and the two tilts follow; the bodies slide and spin within the
        /// plane independently. "rigid" is an assembly MIRROR FEATURE, where
        /// the mirrored instance is a full reflection of its source and every
        /// channel follows.</summary>
        public string MirrorScope;           // "plane" | "rigid"

        /// <summary>Cam couplings only: the cam path's faces triangulated in
        /// global metres, the cam's axis with a point on it (the cam joint's),
        /// and the follower's contact entity. The consumer holds the follower
        /// on the faces itself: a vertex or a roller by projection along its
        /// slide, a flat face by the profile's support function. Written when
        /// the relation probe could not table the cam (a cam free in its
        /// plane, cam-follower2, 2026-09-15) or did not run.</summary>
        public double[] CamAxis;
        public double[] CamOrigin;
        public double[][] CamSurfacePoints;
        public int[][] CamSurfaceTriangles;
        public string FollowerKind;          // vertex | roller | flat
        public double[] FollowerPoint;       // the vertex, the roller's axis point, a point on the flat face
        public double[] FollowerAxis;        // roller: the roller's axis; null otherwise
        public double? FollowerRadius;       // roller (or a ball follower's sphere); null otherwise
        public double[] FollowerNormal;      // flat: the face normal; null otherwise
    }

    public sealed class SourceMate
    {
        public string SwFeature;             // mate feature name, e.g. Concentric3
        public string Type;                  // swMateType_e name, e.g. swMateCONCENTRIC
    }

    public sealed class RigLoop
    {
        public string Id;
        public List<string> MemberJoints = new List<string>();
        public string ClosureJoint;          // the cut edge: re-closed, not parented
        public string SuggestedDriverJoint;

        /// <summary>How the consumer should re-close the cut. "ik" (the
        /// default) is a point coincidence solved by rotating the driven
        /// chain. "aim_pair" is a slider-crank: the two bodies either side of
        /// the cut each hang off their own pin and simply aim at each other,
        /// because no rotational solver can lengthen a slide.</summary>
        public string ClosureKind = "ik";
        public bool Planar;
        public double[] PlaneNormal;         // null when not planar

        /// <summary>How many inputs this ring takes: its own joints' freedom
        /// less what closing the ring spends. One for almost every ring, and
        /// 2 for a five-bar. The consumer solves the closure with the driven
        /// chain, so a ring of mobility m must leave m-1 of that chain's
        /// bones OUT of the solve, for the user to pose.
        ///
        /// Only ever counted for a planar ring, where a joint's contribution
        /// is plain; elsewhere it stays 1, and a ring that reads as rigid
        /// stays 1 as well. Under-counting costs a control the user could
        /// have had. Over-counting takes a constraint away, which is the
        /// error that shows as a mechanism falling apart.</summary>
        public int Mobility = 1;

        /// <summary>Every input the analyzer weighed for this loop, the
        /// chosen one first, each with the cut and closure its choice
        /// implies. A consumer that lets the user pick another input applies
        /// the whole candidate, never the joint alone: the cut sits beside
        /// the driver, so moving one moves the other.</summary>
        public List<RigLoopCandidate> DriverCandidates = new List<RigLoopCandidate>();

        /// <summary>Analysis-only, never serialised: the ring's groups in
        /// ring order, the group the ring hangs from (nearest the root),
        /// and the body the driver poses. The consumer's control rule
        /// (a driver whose posed body another loop's chain solves is no
        /// control) is replayed from these to name a mechanism's input.</summary>
        public List<string> RingGroups;
        public string AnchorGroup;
        public string DriverChildGroup;
    }

    public sealed class RigLoopCandidate
    {
        public string DriverJoint;
        public string ClosureJoint;
        public string ClosureKind;

        public RigLoopCandidate(string driver, string closure, string kind)
        {
            DriverJoint = driver;
            ClosureJoint = closure;
            ClosureKind = kind;
        }
    }

    /// <summary>
    /// Loops that share joints: one degree of freedom, one input. Each
    /// input the mechanism can take is a COMPLETE alternative (every loop
    /// of the mechanism re-chosen with that input, and the joints whose
    /// tree direction turns round), so a consumer switches inputs by
    /// applying an option whole. A per-loop candidate applied on its own
    /// left the other loops on the old input: two drivers on one degree
    /// of freedom, and a tree the loop members no longer described (live
    /// plunger.sldasm, 2026-09-15).
    /// </summary>
    public sealed class RigMechanism
    {
        public string Id;
        public List<string> LoopIds = new List<string>();
        /// <summary>The exporter's own choice first.</summary>
        public List<RigInputOption> Inputs = new List<RigInputOption>();

        /// <summary>Analysis-only, never serialised: this mechanism is a
        /// COUPLED PAIR, not a ring, and its second input is the coupling's
        /// driven half on purpose. A consumer reads the same fact off the
        /// manifest: no loops, and the two inputs are a coupling and its
        /// driver.</summary>
        public bool CouplingPair;
    }

    public sealed class RigInputOption
    {
        public string Joint;
        /// <summary>The mechanism's loops under this input, same ids as
        /// the mechanism's loops, in the same order.</summary>
        public List<RigLoop> Loops = new List<RigLoop>();
        /// <summary>Joints whose parent and child swap under this input
        /// (relative to the manifest's joints), because the tree reaches
        /// them from the other side.</summary>
        public List<string> FlippedJoints = new List<string>();

        /// <summary>Joints whose limits differ under this input: a stroke
        /// limit derived onto a slider-crank's crank belongs to the
        /// crank-driven configuration. A consumer applies these with the
        /// option and restores the manifest's own when it leaves it.</summary>
        public List<RigOptionLimit> JointLimits = new List<RigOptionLimit>();
    }

    public sealed class RigOptionLimit
    {
        public string Joint;
        public JointLimit RotationLimit;     // null = unlimited under this input
        public JointLimit TranslationLimit;
    }

    public sealed class ManifestWarning
    {
        public string Code;                  // see SCHEMA.md for the established codes
        public List<string> Components = new List<string>();
        public List<string> Joints = new List<string>();
        public string Message;
    }
}
