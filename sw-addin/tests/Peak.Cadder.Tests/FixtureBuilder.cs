using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Builds MateGraph fixtures in code. Geometry is stated the way MateReader
    /// reports it after the assembly is solved: both entities of a mate already
    /// agree (same axis, same plane), directions are unit, everything is in the
    /// global frame in metres.
    /// </summary>
    internal static class FixtureBuilder
    {
        public static readonly double[] X = { 1, 0, 0 };
        public static readonly double[] Y = { 0, 1, 0 };
        public static readonly double[] Z = { 0, 0, 1 };

        public static double[] P(double x, double y, double z) => new[] { x, y, z };

        public static MateGraph Graph(GraphComponent[] components, params GraphMate[] mates)
        {
            var g = new MateGraph();
            g.Components.AddRange(components);
            g.Mates.AddRange(mates);
            return g;
        }

        public static GraphComponent Comp(
            string id, string name, bool isFixed = false, bool suppressed = false,
            string fileName = null,
            double[] bboxMin = null, double[] bboxMax = null, double[] at = null)
        {
            var c = new GraphComponent();
            c.Id = id;
            c.Name = name;
            c.Path = name + "-1";
            c.FileName = fileName ?? name + ".sldprt";
            c.Transform = MathOps.Identity4();
            if (at != null)
            {
                c.Transform[0, 3] = at[0];
                c.Transform[1, 3] = at[1];
                c.Transform[2, 3] = at[2];
            }
            c.IsFixed = isFixed;
            c.Suppressed = suppressed;
            c.BboxMin = bboxMin;
            c.BboxMax = bboxMax;
            return c;
        }

        /// <summary>What IComponent2.GetConstrainedStatus returns for a
        /// component SolidWorks has solved to zero remaining freedom: the
        /// component the FeatureManager shows without a "(-)" prefix.</summary>
        public static GraphComponent FullyDefined(GraphComponent c)
        {
            c.ConstrainedStatus = 3;    // swFullyConstrained
            return c;
        }

        public static GraphComponent UnderDefined(GraphComponent c)
        {
            c.ConstrainedStatus = 2;    // swUnderConstrained
            return c;
        }

        /// <summary>What SolidWorks reports with every limit mate taken
        /// out (GraphComponent.StatusFree).</summary>
        public static GraphComponent StillWithLimitsOut(GraphComponent c)
        {
            c.StatusFree = 3;           // swFullyConstrained
            return c;
        }

        public static GraphComponent MovesWithLimitsOut(GraphComponent c)
        {
            c.StatusFree = 2;           // swUnderConstrained
            return c;
        }

        /// <summary>Fixed inside its subassembly document: rigid to the
        /// sub's frame, never to the world.</summary>
        public static GraphComponent InSubFixed(GraphComponent c)
        {
            c.FixedInSubassembly = true;
            return c;
        }

        /// <summary>Places a component inside a walked (flexible) subassembly
        /// node, which is the only way a walked component has a parent.</summary>
        public static GraphComponent Inside(GraphComponent c, string parentId)
        {
            c.ParentId = parentId;
            return c;
        }

        public static GraphMate Mate(string feature, string typeName, params GraphMateEntity[] entities)
        {
            var m = new GraphMate();
            m.FeatureName = feature;
            m.TypeName = typeName;
            m.Entities.AddRange(entities);
            return m;
        }

        /// <summary>Turns a mate into a limit mate: Min != Max is what makes it
        /// one, CurrentValue is the as-mated dimension the manifest exports as
        /// value_at_rest.</summary>
        public static GraphMate WithRange(GraphMate m, double min, double max, double current)
        {
            m.MinimumVariation = min;
            m.MaximumVariation = max;
            m.CurrentValue = current;
            return m;
        }

        /// <summary>The "Flip dimension" tick, as MateReader reads it off the
        /// mate feature data.</summary>
        public static GraphMate WithFlippedDimension(GraphMate m)
        {
            m.DimensionFlipped = true;
            return m;
        }

        // ── Entities ────────────────────────────────────────────────────────

        public static GraphMateEntity Cylinder(string compId, double[] dir, double[] point, double radius = 0.004)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "cylinder";
            e.Direction = (double[])dir.Clone();
            e.Point = (double[])point.Clone();
            e.Radius = radius;
            return e;
        }

        public static GraphMateEntity PlaneEnt(string compId, double[] normal, double[] point)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "plane";
            e.Direction = (double[])normal.Clone();
            e.Point = (double[])point.Clone();
            return e;
        }

        public static GraphMateEntity PointEnt(string compId, double[] point)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "point";
            e.Point = (double[])point.Clone();
            return e;
        }

        /// <summary>A selected vertex as live SolidWorks delivers it:
        /// ReferenceType 0 with swSelVERTICES, which EntityKind names
        /// "vertex", point only, the direction slots are filler (live
        /// corpus 13/16, 2026-08-23).</summary>
        public static GraphMateEntity VertexEnt(string compId, double[] point)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "vertex";
            e.Point = (double[])point.Clone();
            return e;
        }

        /// <summary>A model edge with its direction: either delivered as a
        /// line-typed entity, or recovered by MateReader from the underlying
        /// curve when SolidWorks reduced the edge to a directionless point
        /// (live corpus 16 pt2, 2026-08-23).</summary>
        public static GraphMateEntity EdgeEnt(string compId, double[] dir, double[] point)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "edge";
            e.Direction = (double[])dir.Clone();
            e.Point = (double[])point.Clone();
            return e;
        }

        /// <summary>A temporary axis / datum axis entity, as a coincident mate
        /// between two axes records them (the live hinge4 case).</summary>
        public static GraphMateEntity AxisEnt(string compId, double[] dir, double[] point)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "axis";
            e.Direction = (double[])dir.Clone();
            e.Point = (double[])point.Clone();
            return e;
        }

        /// <summary>The sampled-curve side of a coincidence or path mate onto
        /// assembly-owned sketch geometry: no component, no direction, the
        /// polyline itself lives on the MATE (GraphMate.PathPoints), the way
        /// MateReader's curve recovery stores it (live corpus 16/17,
        /// 2026-08-23).</summary>
        public static GraphMateEntity CurveEnt(double[] point = null)
        {
            var e = new GraphMateEntity();
            e.ComponentId = null;
            e.EntityTypeName = "curve";
            e.Point = point != null ? (double[])point.Clone() : new double[3];
            return e;
        }

        /// <summary>A face MateReader triangulated because no analytic joint
        /// describes it: a torus, a fillet, a loft.</summary>
        public static GraphMateEntity SurfaceEnt(
            string compId, double[][] points, int[][] triangles)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "surface";
            e.SurfacePoints = points;
            e.SurfaceTriangles = triangles;
            return e;
        }

        /// <summary>A quad of the z = height plane, as two triangles: the
        /// smallest patch a surface joint can ride, standing in for whatever
        /// free-form face the live reader tessellated.</summary>
        public static GraphMateEntity PatchEnt(string compId, double height)
        {
            var points = new[]
            {
                new[] { -0.05, -0.05, height },
                new[] { 0.05, -0.05, height },
                new[] { 0.05, 0.05, height },
                new[] { -0.05, 0.05, height },
            };
            var triangles = new[]
            {
                new[] { 0, 1, 2 },
                new[] { 0, 2, 3 },
            };
            return SurfaceEnt(compId, points, triangles);
        }

        /// <summary>A conical face after MateReader's surface retype: axis
        /// direction, a point on the axis, and the surface's half-angle
        /// (live corpus 15, 2026-08-23, the raw entity arrives as a CIRCLE
        /// with the half-angle in the radius slot).</summary>
        public static GraphMateEntity ConeEnt(
            string compId, double[] dir, double[] point, double halfAngle)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "cone";
            e.Direction = (double[])dir.Clone();
            e.Point = (double[])point.Clone();
            e.HalfAngle = halfAngle;
            return e;
        }

        public static GraphMateEntity SphereEnt(string compId, double[] center, double radius = 0.01)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "sphere";
            e.Point = (double[])center.Clone();
            e.Radius = radius;
            return e;
        }

        /// <summary>An entity whose kind fell outside swMateEntityTypes_e but
        /// whose EntityParams still carried a (leftover) direction: the shape
        /// a misreported spherical face arrives in (live corpus 04, 2026-08-22).</summary>
        public static GraphMateEntity UnknownEnt(string compId, double[] point, double[] dir = null)
        {
            var e = new GraphMateEntity();
            e.ComponentId = compId;
            e.EntityTypeName = "unknown";
            e.Point = (double[])point.Clone();
            if (dir != null) e.Direction = (double[])dir.Clone();
            return e;
        }

        // ── Common mates ────────────────────────────────────────────────────

        public static GraphMate Concentric(string feature, string compA, string compB, double[] dir, double[] point)
        {
            return Mate(feature, "swMateCONCENTRIC",
                Cylinder(compA, dir, point), Cylinder(compB, dir, point));
        }

        public static GraphMate CoincidentPlanes(string feature, string compA, string compB, double[] normal, double[] point)
        {
            return Mate(feature, "swMateCOINCIDENT",
                PlaneEnt(compA, normal, point), PlaneEnt(compB, normal, point));
        }

        public static GraphMate ParallelPlanes(string feature, string compA, string compB, double[] normal, double[] point)
        {
            return Mate(feature, "swMatePARALLEL",
                PlaneEnt(compA, normal, point), PlaneEnt(compB, normal, point));
        }

        /// <summary>Solved limit-mate geometry: the measurement faces sit the
        /// current dimension APART, identical entities would put the mate at
        /// its zero, where the dimension's direction is undefined. The B-side
        /// plane is offset by <paramref name="current"/> along the normal.</summary>
        public static GraphMate DistanceLimit(
            string feature, string compA, string compB, double[] normal, double[] point,
            double min, double max, double current)
        {
            var pointB = new[]
            {
                point[0] + current * normal[0],
                point[1] + current * normal[1],
                point[2] + current * normal[2],
            };
            return WithRange(
                Mate(feature, "swMateDISTANCE", PlaneEnt(compA, normal, point), PlaneEnt(compB, normal, pointB)),
                min, max, current);
        }

        /// <summary>An angle limit mate measures between two faces whose
        /// normals sit off the rotation axis: that is what lets the angle
        /// change as the joint spins. The two normals are given separately
        /// because in the solved rest pose they differ by the current angle;
        /// their cross product against the axis is what fixes the direction
        /// the dimension grows in.</summary>
        public static GraphMate AngleLimit(
            string feature, string compA, string compB, double[] faceNormalA, double[] faceNormalB,
            double[] point, double min, double max, double current)
        {
            return WithRange(
                Mate(feature, "swMateANGLE", PlaneEnt(compA, faceNormalA, point), PlaneEnt(compB, faceNormalB, point)),
                min, max, current);
        }
    }
}
