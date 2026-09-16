using System.Collections.Generic;

namespace Peak.SwToBlender.Core.Model
{
    /// <summary>
    /// The classifier's input: everything MateReader saw, and nothing that
    /// needs a live SolidWorks to interpret. This type is also the fixture
    /// format: the recorder serialises one MateGraph per test assembly, and
    /// the unit tests run RigidGrouper/JointClassifier/LoopAnalyzer on the
    /// recording with no SolidWorks present.
    /// </summary>
    public sealed class MateGraph
    {
        public List<GraphComponent> Components = new List<GraphComponent>();
        public List<GraphMate> Mates = new List<GraphMate>();

        /// <summary>Assembly mirror features: a source occurrence and the
        /// instance mirrored from it. No mate connects them, but the
        /// relation is real and SolidWorks keeps it.</summary>
        public List<GraphMirrorPair> MirrorPairs = new List<GraphMirrorPair>();
    }

    /// <summary>
    /// One source/mirrored occurrence pair from an assembly mirror feature.
    /// The mirrored instance is a FULL reflection of its source: position and
    /// orientation both, whether the feature made an opposite-hand part or
    /// only repositioned the original. Geometry never reaches the manifest, so
    /// the two cases are the same relation here.
    /// </summary>
    public sealed class GraphMirrorPair
    {
        public string FeatureName;
        public string SourceComponentId;
        public string MirroredComponentId;
        public double[] PlanePoint;          // global metres
        public double[] PlaneNormal;         // unit, global
    }

    public sealed class GraphComponent
    {
        public string Id;                 // same id that ends up in the manifest
        public string Path;               // instance path, Gripper/Jaw-1
        public string Name;               // component name without instance suffix
        public string FileName;           // model file name, diagnostics only
        public double[,] Transform;       // 4x4 row-major, global, metres
        public bool IsFixed;
        public bool Suppressed;
        public string Solving;            // "rigid" | "flexible" | null (parts)

        /// <summary>The walked component this one sits under, null at top
        /// level. Only children of flexible subassemblies are walked, so a
        /// non-null parent is always a flexible subassembly node.</summary>
        public string ParentId;

        /// <summary>Fixed INSIDE its (flexible) subassembly document. Rigid
        /// to the subassembly's frame, never to the world: the top-context
        /// occurrence reports IsFixed()=false for these (live corpus 07,
        /// 2026-08-22), so the walker queries the subassembly's own tree.</summary>
        public bool FixedInSubassembly;

        /// <summary>IComponent2.GetConstrainedStatus (swConstrainedStatus_e),
        /// 0 when it could not be read. SolidWorks evaluates this in the
        /// TOP-LEVEL solve: it is the "(-)" prefix the FeatureManager shows,
        /// so swFullyConstrained means the occurrence cannot move in the world,
        /// whatever its mates alone would allow. The mate analysis cannot
        /// derive this: it sees one component pair at a time, and a part pinned
        /// by three neighbours is free against each of them separately (live
        /// ClampRig, 2026-08-24: 83 rigid groups and 88 joints for an
        /// assembly whose parts almost all sit fully defined).</summary>
        public int ConstrainedStatus;

        /// <summary>Children of a flexible subassembly only: actual pose =
        /// this delta × the pose the sub DOCUMENT stores. Internal mates are
        /// read through the sub document, so their entity geometry and
        /// dimension values describe the document pose, not the flexed
        /// instance. Live corpus 07 flexible-sub2 (2026-08-22): the hinge
        /// flexed to 75° still exported value_at_rest = 30°, the document's
        /// angle. Null when the instance sits at the document pose.</summary>
        public double[,] MatePoseDelta;
        public double[] BboxMin;          // global-frame box, null when unavailable
        public double[] BboxMax;
    }

    /// <summary>
    /// One mate feature. TypeName is the swMateType_e constant name
    /// (swMateCONCENTRIC, ...): classification keys on the name, the raw
    /// value is kept only for diagnostics because the enum has grown between
    /// SolidWorks versions.
    /// </summary>
    public sealed class GraphMate
    {
        public string FeatureName;        // Concentric3
        public string TypeName;           // swMateCONCENTRIC
        public int TypeValue;
        public int Alignment;             // swMateAlign_e value
        public bool Flipped;
        public bool Suppressed;

        /// <summary>Human-readable error state from the mate feature
        /// (IFeature.GetErrorCode2), null when the mate is healthy. An
        /// errored or over-defining mate is one SolidWorks itself is not
        /// solving faithfully, so the export refuses to guess and aborts
        /// (live corpus 06 parallelogram family, 2026-08-22).</summary>
        public string Error;

        /// <summary>Limit mates: the allowed range. Equal values = not a limit mate.</summary>
        public double MinimumVariation;
        public double MaximumVariation;

        /// <summary>The mate's dimension value in the as-mated configuration
        /// (distance in metres or angle in radians). NaN when there is none.</summary>
        public double CurrentValue = double.NaN;

        /// <summary>The "Flip dimension" tick on distance/angle mates
        /// (I*MateFeatureData.FlipDimension): the dimension measures to the
        /// OTHER side, so its sense against the mate geometry is negated.
        /// Only the degenerate parked pose needs it: at any readable pose
        /// the entities themselves sit on the flipped side and the geometric
        /// sign already reports the flipped sense (live corpus 01 hinge5,
        /// 2026-08-23).</summary>
        public bool DimensionFlipped;

        /// <summary>Gear/rack-pinion/screw/linear-coupler parameters, from the
        /// mate feature data. Null when the mate carries none.</summary>
        public double? CouplingRatio;     // legacy single-number ratio (recorded fixtures)
        public double? MetersPerRadian;   // rack and pinion
        public double? LeadMPerRev;       // screw

        /// <summary>Gear and linear-coupler ratio, kept as the raw pair in
        /// ENTITY order plus the reverse tick, because the signed driven/driver
        /// ratio depends on which side ends up the driver and on the two mount
        /// joints' axis senses: only the classifier knows those. Live corpus
        /// 08 (2026-08-22) pinned the convention: numerator:denominator is the
        /// ANGULAR ratio θ(entity1):θ(entity2), so entity2 follows entity1 at
        /// denominator/numerator.</summary>
        public double? CouplingNumerator;
        public double? CouplingDenominator;
        public bool CouplingReverse;

        /// <summary>Profile-centre mates: the "lock rotation" tick. Only the
        /// feature data carries it: the raw entity params are byte-identical
        /// locked or unlocked (live corpus 05, 2026-08-22).</summary>
        public bool LockRotation;

        /// <summary>Slot mates: swSlotMateConstraintOptions_e (0 free,
        /// 1 centered, 2 distance, 3 percent). −1 when unread: treated as
        /// free, the freer and therefore safe reading.</summary>
        public int SlotConstraint = -1;

        /// <summary>Path mates: the path sampled into a polyline, global
        /// metres. SolidWorks has no feature data interface for path mates,
        /// so MateReader digs the curve out of the mate entities' underlying
        /// edges/sketch segments; null when that fails.</summary>
        public double[][] PathPoints;
        public bool PathClosed;

        /// <summary>Cam-follower mates: the cam path's faces triangulated in
        /// global metres, and the component that owns them. The mate
        /// entities show one cam face and the follower; the feature data
        /// lists every face of the path. The consumer holds the follower on
        /// these when the relation probe cannot table the cam (a cam free
        /// in its plane, cam-follower2, 2026-09-15). Null when unread.</summary>
        public string CamComponentId;
        public double[][] CamSurfacePoints;
        public int[][] CamSurfaceTriangles;

        public List<GraphMateEntity> Entities = new List<GraphMateEntity>();
    }

    /// <summary>
    /// One mate entity, geometry already lifted to the global frame.
    /// EntityTypeName is the swSelectType_e-ish kind MateReader resolved
    /// ("cylinder", "plane", "point", "cone", "sphere", "surface", "axis",
    /// "edge", "curve", "vertex", "unknown").
    /// </summary>
    public sealed class GraphMateEntity
    {
        public string ComponentId;        // null when the entity sits on the assembly itself
        public string EntityTypeName;
        public double[] Point;            // a point on the entity, global, metres
        public double[] Direction;        // unit axis/normal, global; null for point/sphere
        public double Radius;             // cylinders/spheres; 0 otherwise

        /// <summary>Conical faces only, radians, from the face surface's
        /// ConeParams: a cone tangent to a plane holds its axis tilted at
        /// exactly this angle out of the plane. Live corpus 15 (2026-08-23):
        /// conical faces arrive as CIRCLE entities with the half-angle in
        /// the radius slot, so the surface read is what makes it reliable.</summary>
        public double HalfAngle;

        /// <summary>The face triangulated, global metres, for a surface no
        /// joint type models in closed form: a torus, a loft, any B-surface.
        /// SolidWorks mates a point to such a face happily, so the shape
        /// itself has to travel: the consumer holds the point on the mesh.
        /// Null for every analytic surface, which stays exact.</summary>
        public double[][] SurfacePoints;
        public int[][] SurfaceTriangles;
    }
}
