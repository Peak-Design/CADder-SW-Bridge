using System.Collections.Generic;

namespace Peak.SwToBlender.Core.Model
{
    /// <summary>
    /// The manifest DOM. This namespace has no dependency on
    /// SolidWorks.Interop.* — the Sw\ layer fills it, ManifestWriter emits it,
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
        public List<ManifestWarning> Warnings = new List<ManifestWarning>();
    }

    public sealed class GeneratorInfo
    {
        public string Name = "Peak.SwToBlender";
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
        public bool IsFastener;
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
        /// fallback for surfaces no analytic joint describes — a torus, a
        /// fillet, a loft — which SolidWorks mates to as readily as a
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
        public string Kind;                  // gear | rack_pinion | screw | linear_coupler | mirror
        public string DriverJoint;           // null for screw self-coupling
        public double? Ratio;                // gear (rad/rad, negative = reversal), linear_coupler (m/m)
        public double? MetersPerRadian;      // rack_pinion
        public double? LeadMPerRev;          // screw

        /// <summary>Mirror couplings only: the driven joint's body poses as
        /// the exact mirror image of the driver's body across this plane
        /// (live corpus 14 sym4, 2026-08-23 — a symmetric mate between two
        /// otherwise unmated bodies).</summary>
        public double[] MirrorPlanePoint;
        public double[] MirrorPlaneNormal;   // unit, global
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
        public string ClosureJoint;          // the cut edge — closed by IK, not parenting
        public string SuggestedDriverJoint;
        public bool Planar;
        public double[] PlaneNormal;         // null when not planar
    }

    public sealed class ManifestWarning
    {
        public string Code;                  // see SCHEMA.md for the established codes
        public List<string> Components = new List<string>();
        public List<string> Joints = new List<string>();
        public string Message;
    }
}
