using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The UV map a part gets over the live link.
    ///
    /// SolidWorks hands out the surface's own parameters, and those are in
    /// the surface's own units: metres on a plane, radians by metres on a
    /// cylinder. Written straight into a UV map, the cylinder comes out
    /// hundreds of times too wide and the plane beside it looks right,
    /// which is what a stretched island IS.
    ///
    /// A plane, a cylinder and a cone are developable, so each is flattened
    /// exactly. Nothing else is, and there one scale in u and one in v is
    /// the best a single frame can do.
    /// </summary>
    public class SurfaceUvTests
    {
        private const double Radius = 0.02;      // 20 mm
        private const double Height = 0.01;      // 10 mm

        [Fact]
        public void ACylinderComesBackAsArcLengthByHeight()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);

            // As it arrives: u runs over half a turn, which is 3.14 against
            // a height of 0.01. That is the island that looks like spaghetti.
            Assert.InRange(Span(mesh, 0), Math.PI - 1e-9, Math.PI + 1e-9);

            var marks = faceOf.ToArray();
            int after = SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                                          new[] { 0 }, Fitted(1), ref marks, null);

            Assert.Equal(mesh.VertexCount, after);
            // Half the circumference by the height, both in metres.
            Near(Radius * Math.PI, Span(mesh, 0), 0.005);
            Near(Height, Span(mesh, 1), 0.005);
        }

        [Fact]
        public void ACylinderTakesItsRadiusFromTheSurfaceItself()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);

            var charts = Analytic(SurfaceUv.Cylinder,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, Radius });
            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, charts, ref marks, null);

            // A cylinder unrolls into a rectangle with no error at all, so
            // this is the true half circumference and not a fit near it.
            Assert.True(charts[0].Unrolled, "the cylinder was not unrolled");
            Near(Radius * Math.PI, Span(mesh, 0), 1e-12);
            Near(Height, Span(mesh, 1), 1e-12);
        }

        [Fact]
        public void APlaneIsAlreadyInMetresAndIsLeftAtItsOwnSize()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Plane(mesh, faceOf, 0, 0.05, 0.03);

            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, Fitted(1), ref marks, null);

            Near(0.05, Span(mesh, 0), 1e-9);
            Near(0.03, Span(mesh, 1), 1e-9);
        }

        [Fact]
        public void EveryChartStartsAtItsOwnCornerOfTheOrigin()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Plane(mesh, faceOf, 0, 0.05, 0.03);
            Plane(mesh, faceOf, 1, 0.05, 0.03);

            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 1 }, Fitted(2), ref marks, null);

            // Two faces of one size would otherwise write exactly the same
            // UVs, and Blender reads that as one island folded on itself.
            double first = Low(mesh, faceOf, 0, 0);
            double second = Low(mesh, faceOf, 1, 0);
            Assert.NotEqual(first, second);
            Assert.InRange(first, 0.0, SurfaceUv.ChartNudge);
            Assert.InRange(second, 0.0, SurfaceUv.ChartNudge);
        }

        [Fact]
        public void TwoPatchesOfOneCylinderLineUpInsteadOfOverlapping()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            Cylinder(mesh, faceOf, 1, Math.PI, 2.0 * Math.PI, 24);

            // One chart, because the two faces lie on one cylinder.
            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 0 }, Fitted(1), ref marks, null);

            double halfWay = Radius * Math.PI;
            Near(halfWay, High(mesh, faceOf, 0, 0) - Low(mesh, faceOf, 0, 0), 0.005);
            // The second patch starts where the first ends, so the join is
            // a join and not a fold.
            Near(High(mesh, faceOf, 0, 0), Low(mesh, faceOf, 1, 0), 0.01);
        }

        [Fact]
        public void TwoPatchesInTwoChartsLandOnTopOfEachOther()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            Cylinder(mesh, faceOf, 1, Math.PI, 2.0 * Math.PI, 24);

            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 1 }, Fitted(2), ref marks, null);

            // Which is what the chart is FOR: apart, they both start at the
            // origin and cover the same ground.
            Near(Low(mesh, faceOf, 0, 0), Low(mesh, faceOf, 1, 0),
                 SurfaceUv.ChartNudge * 2.0);
        }

        [Fact]
        public void AConeUnrollsIntoAFanWithNoStretchAnywhere()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            double half = 0.35;                       // about 20 degrees
            Cone(mesh, faceOf, 0, half, 0.01, 0.05, 0.0, 2.0 * Math.PI, 48, 6);

            var charts = Analytic(SurfaceUv.Cone,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, 0.0, half });
            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, charts, ref marks, null);

            Assert.True(charts[0].Unrolled, "the cone was not unrolled");
            // A cone is developable, so flattening it costs NOTHING: every
            // edge keeps its length. One scale in u and one in v cannot do
            // that, because the radius changes up the cone.
            double low, high;
            Ratios(mesh, out low, out high);
            Assert.True(low > 0.99 && high < 1.01,
                        "the fan stretches: " + low + " to " + high);
        }

        [Fact]
        public void AScaledConeStretchesWhereTheFanDoesNot()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cone(mesh, faceOf, 0, 0.35, 0.01, 0.05, 0.0, 2.0 * Math.PI, 48, 6);

            // The same cone with nothing said about the surface: one scale
            // in u for a face whose radius runs from 3.4 mm to 17 mm.
            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, Fitted(1), ref marks, null);

            double low, high;
            Ratios(mesh, out low, out high);
            Assert.True(low < 0.7 || high > 1.4,
                        "a scaled cone should not come out exact: "
                        + low + " to " + high);
        }

        [Fact]
        public void ASurfaceWhoseParametersAreNotWhatTheyShouldBeIsFitted()
        {
            // A cylinder whose u is NOT an angle: a parameterisation this
            // does not expect. The surface says one thing, the triangles
            // say another, and the triangles are what was measured.
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            for (int v = 0; v < mesh.VertexCount; v++)
                mesh.Uvs[v * 2] *= 100.0;

            var charts = Analytic(SurfaceUv.Cylinder,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, Radius });
            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, charts, ref marks, null);

            Assert.False(charts[0].Unrolled,
                         "the surface was believed over the triangles");
            Near(Radius * Math.PI, Span(mesh, 0), 0.005);
        }

        [Fact]
        public void AFaceWhoseScaleChangesIsSizedWhereMostOfItIs()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Ramp(mesh, faceOf, 0);

            var marks = faceOf.ToArray();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, Fitted(1), ref marks, null);

            // A worm thread or a spline runs at one scale over most of its
            // area and at another over a corner of it. Least squares
            // answers for the corner, because it works on squared lengths,
            // and the rest of the face then comes out many times too small.
            // What a texture is looked at on is the rest of the face.
            Near(1.0, MedianRatio(mesh), 0.4);
        }

        [Fact]
        public void AClosedCylinderIsCutOpenAlongItsSeam()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Ring(mesh, faceOf, 0, 24);
            int before = mesh.VertexCount;

            var charts = Analytic(SurfaceUv.Cylinder,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, Radius });
            var marks = faceOf.ToArray();
            int after = SurfaceUv.Rewrite(mesh, 0, before, 0, new[] { 0 },
                                          charts, ref marks, null);

            // The ring of triangles that closes the cylinder joined the far
            // side of the seam to the near side. In the map that is the
            // whole circumference across a face a millimetre wide: a smear
            // of the entire texture down one line of the part.
            Assert.True(after > before, "nothing was cut open");
            Assert.Equal(after, mesh.VertexCount);
            Assert.Equal(after, marks.Length);
            for (int v = before; v < after; v++)
                Assert.Equal(0, marks[v]);

            // Nothing moved: every copy sits exactly where the point it
            // came from does, which is what the closure count needs.
            for (int v = before; v < after; v++)
                Assert.True(Somewhere(mesh, v, before),
                            "a copy landed off the part");

            // And the map is now as clean as an open cylinder's.
            double low, high;
            Ratios(mesh, out low, out high);
            Assert.True(low > 0.99 && high < 1.01,
                        "the seam still runs backwards: " + low + " to " + high);
        }

        [Fact]
        public void APartOfACylinderHasNoSeamToCut()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            int before = mesh.VertexCount;

            var charts = Analytic(SurfaceUv.Cylinder,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, Radius });
            var marks = faceOf.ToArray();
            Assert.Equal(before, SurfaceUv.Rewrite(mesh, 0, before, 0,
                                                   new[] { 0 }, charts,
                                                   ref marks, null));
            Assert.Equal(before, mesh.VertexCount);
        }

        [Fact]
        public void ASurfaceWhoseUIsNotAnAngleIsLeftOnItsOwnBranch()
        {
            // Nothing says a chart this could not name is reported modulo
            // anything, so there is no branch to choose.
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Ring(mesh, faceOf, 0, 24);
            int before = mesh.VertexCount;

            var marks = faceOf.ToArray();
            Assert.Equal(before, SurfaceUv.Rewrite(mesh, 0, before, 0,
                                                   new[] { 0 }, Fitted(1),
                                                   ref marks, null));
        }

        [Fact]
        public void AnAngleReportedInPiecesIsPutBackTogether()
        {
            // A face that straddles the start of the angle, which is what
            // every cylinder cut across the seam does, and what a worm
            // wheel's tooth faces do everywhere: the angle is reported
            // modulo a turn, so two neighbours a millimetre apart on the
            // part come back a whole turn apart in u.
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 5.5, 5.5 + Math.PI, 24);
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                double u = mesh.Uvs[v * 2];
                while (u >= 2.0 * Math.PI) u -= 2.0 * Math.PI;
                mesh.Uvs[v * 2] = u;
            }
            Assert.True(Span(mesh, 0) > 5.0, "the fixture does not straddle");

            var charts = Analytic(SurfaceUv.Cylinder,
                                  new[] { 0.0, 0, 0, 0, 0, 1.0, Radius });
            var marks = faceOf.ToArray();
            int after = SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                                          new[] { 0 }, charts, ref marks, null);

            // Put back on one branch it is the same half cylinder as ever,
            // in one piece, with nothing copied.
            Assert.Equal(mesh.VertexCount, after);
            Near(Radius * Math.PI, Span(mesh, 0), 0.005);
            double low, high;
            Ratios(mesh, out low, out high);
            Assert.True(high < 1.01, "it is still in pieces: up to " + high);
        }

        [Fact]
        public void AVertexNoFaceClaimedKeepsWhatSolidWorksGaveIt()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 8);
            var marks = faceOf.ToArray();
            marks[marks.Length - 1] = -1;
            double was = mesh.Uvs[(marks.Length - 1) * 2];

            var said = new List<string>();
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0, new[] { 0 },
                              Fitted(1), ref marks, said.Add);

            Assert.Equal(was, mesh.Uvs[(marks.Length - 1) * 2]);
            Assert.Contains(said, line => line.Contains("belonged to no face"));
        }

        [Fact]
        public void ABodyThisCannotReadIsLeftExactlyAsItWas()
        {
            var mesh = new MeshDefinition();
            var nothing = new int[0];
            Assert.Equal(0, SurfaceUv.Rewrite(mesh, 0, 0, 0, new[] { 0 },
                                              Fitted(1), ref nothing, null));
            var four = new[] { 0, 0, 0, 0 };
            Assert.Equal(4, SurfaceUv.Rewrite(mesh, 0, 4, 0, null,
                                              Fitted(1), ref four, null));
        }

        // Fixtures.

        /// <summary>Charts that say nothing about their surface, so every
        /// one of them is fitted from the triangles.</summary>
        private static List<SurfaceUv.Chart> Fitted(int count)
        {
            var charts = new List<SurfaceUv.Chart>();
            for (int i = 0; i < count; i++)
                charts.Add(new SurfaceUv.Chart(null, null));
            return charts;
        }

        private static List<SurfaceUv.Chart> Analytic(string kind, double[] values)
        {
            return new List<SurfaceUv.Chart> { new SurfaceUv.Chart(kind, values) };
        }

        /// <summary>A band of a cylinder about Z, tessellated the way
        /// SolidWorks does: u is the angle in radians, v the height in
        /// metres.</summary>
        private static void Cylinder(
            MeshDefinition mesh, List<int> faceOf, int face,
            double from, double to, int steps)
        {
            int first = mesh.VertexCount;
            for (int i = 0; i <= steps; i++)
                Column(mesh, faceOf, face, from + (to - from) * i / steps);
            for (int i = 0; i < steps; i++)
            {
                int a = first + i * 2, b = first + (i + 1) * 2;
                Triangle(mesh, a, a + 1, b);
                Triangle(mesh, a + 1, b + 1, b);
            }
        }

        /// <summary>A WHOLE cylinder as SolidWorks tessellates one: ONE
        /// face, u from 0 to a whisker under a turn, and a last ring of
        /// triangles that joins that whisker back to 0.</summary>
        private static void Ring(MeshDefinition mesh, List<int> faceOf,
                                 int face, int steps)
        {
            int first = mesh.VertexCount;
            for (int i = 0; i < steps; i++)
                Column(mesh, faceOf, face, 2.0 * Math.PI * i / steps);
            for (int i = 0; i < steps; i++)
            {
                int a = first + i * 2;
                int b = first + ((i + 1) % steps) * 2;   // the last one wraps
                Triangle(mesh, a, a + 1, b);
                Triangle(mesh, a + 1, b + 1, b);
            }
        }

        private static void Column(MeshDefinition mesh, List<int> faceOf,
                                   int face, double angle)
        {
            for (int row = 0; row < 2; row++)
            {
                double z = row * Height;
                mesh.Positions.Add(Radius * Math.Cos(angle));
                mesh.Positions.Add(Radius * Math.Sin(angle));
                mesh.Positions.Add(z);
                mesh.Normals.Add(Math.Cos(angle));
                mesh.Normals.Add(Math.Sin(angle));
                mesh.Normals.Add(0.0);
                mesh.Uvs.Add(angle);
                mesh.Uvs.Add(z);
                faceOf.Add(face);
            }
        }

        /// <summary>A band of a cone about Z with its apex at the origin:
        /// u is the angle in radians and v the distance down the slant.
        /// </summary>
        private static void Cone(
            MeshDefinition mesh, List<int> faceOf, int face, double half,
            double near, double far, double from, double to,
            int steps, int rows)
        {
            double sin = Math.Sin(half), cos = Math.Cos(half);
            int first = mesh.VertexCount;
            for (int i = 0; i <= steps; i++)
            {
                double angle = from + (to - from) * i / steps;
                for (int row = 0; row <= rows; row++)
                {
                    double slant = near + (far - near) * row / rows;
                    mesh.Positions.Add(slant * sin * Math.Cos(angle));
                    mesh.Positions.Add(slant * sin * Math.Sin(angle));
                    mesh.Positions.Add(slant * cos);
                    mesh.Normals.Add(cos * Math.Cos(angle));
                    mesh.Normals.Add(cos * Math.Sin(angle));
                    mesh.Normals.Add(-sin);
                    mesh.Uvs.Add(angle);
                    mesh.Uvs.Add(slant);
                    faceOf.Add(face);
                }
            }
            int stride = rows + 1;
            for (int i = 0; i < steps; i++)
                for (int row = 0; row < rows; row++)
                {
                    int a = first + i * stride + row;
                    Triangle(mesh, a, a + 1, a + stride);
                    Triangle(mesh, a + 1, a + stride + 1, a + stride);
                }
        }

        /// <summary>A flat quad, where the parameters are already metres.
        /// </summary>
        private static void Plane(
            MeshDefinition mesh, List<int> faceOf, int face,
            double wide, double tall)
        {
            int first = mesh.VertexCount;
            double[,] corners = { { 0, 0 }, { wide, 0 }, { wide, tall }, { 0, tall } };
            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(corners[i, 0]);
                mesh.Positions.Add(corners[i, 1]);
                mesh.Positions.Add(0.0);
                mesh.Normals.Add(0.0);
                mesh.Normals.Add(0.0);
                mesh.Normals.Add(1.0);
                mesh.Uvs.Add(corners[i, 0]);
                mesh.Uvs.Add(corners[i, 1]);
                faceOf.Add(face);
            }
            Triangle(mesh, first, first + 1, first + 2);
            Triangle(mesh, first, first + 2, first + 3);
        }

        /// <summary>A strip that steps one unit of u at a time, where two
        /// hundred steps span half a millimetre each and one spans fifty.
        /// What a surface whose parameters run at two scales looks like to
        /// the tessellation: most of the face at one, a corner of it at the
        /// other.</summary>
        private static void Ramp(MeshDefinition mesh, List<int> faceOf, int face)
        {
            const int Steps = 201;
            int first = mesh.VertexCount;
            double x = 0.0;
            for (int i = 0; i <= Steps; i++)
            {
                for (int row = 0; row < 2; row++)
                {
                    mesh.Positions.Add(x);
                    mesh.Positions.Add(row * 0.002);
                    mesh.Positions.Add(0.0);
                    mesh.Normals.Add(0.0);
                    mesh.Normals.Add(0.0);
                    mesh.Normals.Add(1.0);
                    mesh.Uvs.Add(i);
                    mesh.Uvs.Add(row);
                    faceOf.Add(face);
                }
                x += i == Steps - 1 ? 0.05 : 0.0005;
            }
            for (int i = 0; i < Steps; i++)
            {
                int a = first + i * 2;
                Triangle(mesh, a, a + 1, a + 2);
                Triangle(mesh, a + 1, a + 3, a + 2);
            }
        }

        private static void Triangle(MeshDefinition mesh, int a, int b, int c)
        {
            mesh.Triangles.Add(a);
            mesh.Triangles.Add(b);
            mesh.Triangles.Add(c);
            mesh.TriangleMaterials.Add(0);
        }

        // Measures.

        private static double Span(MeshDefinition mesh, int channel)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                double value = mesh.Uvs[v * 2 + channel];
                if (value < low) low = value;
                if (value > high) high = value;
            }
            return high - low;
        }

        private static double Low(
            MeshDefinition mesh, List<int> faceOf, int face, int channel)
        {
            double low = double.MaxValue;
            for (int v = 0; v < faceOf.Count; v++)
                if (faceOf[v] == face && mesh.Uvs[v * 2 + channel] < low)
                    low = mesh.Uvs[v * 2 + channel];
            return low;
        }

        private static double High(
            MeshDefinition mesh, List<int> faceOf, int face, int channel)
        {
            double high = double.MinValue;
            for (int v = 0; v < faceOf.Count; v++)
                if (faceOf[v] == face && mesh.Uvs[v * 2 + channel] > high)
                    high = mesh.Uvs[v * 2 + channel];
            return high;
        }

        /// <summary>The smallest and largest ratio of UV length to real
        /// length over every triangle edge. One and one is an isometry.
        /// </summary>
        private static void Ratios(MeshDefinition mesh,
                                   out double low, out double high)
        {
            low = double.MaxValue;
            high = double.MinValue;
            foreach (double ratio in Each(mesh))
            {
                if (ratio < low) low = ratio;
                if (ratio > high) high = ratio;
            }
        }

        /// <summary>The middle of how far the UV map is from the part: one
        /// is a texture that does not stretch.</summary>
        private static double MedianRatio(MeshDefinition mesh)
        {
            var found = new List<double>(Each(mesh));
            found.Sort();
            return found[found.Count / 2];
        }

        private static IEnumerable<double> Each(MeshDefinition mesh)
        {
            for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = mesh.Triangles[t + k];
                    int b = mesh.Triangles[t + (k + 1) % 3];
                    double du = mesh.Uvs[b * 2] - mesh.Uvs[a * 2];
                    double dv = mesh.Uvs[b * 2 + 1] - mesh.Uvs[a * 2 + 1];
                    double dx = mesh.Positions[b * 3] - mesh.Positions[a * 3];
                    double dy = mesh.Positions[b * 3 + 1] - mesh.Positions[a * 3 + 1];
                    double dz = mesh.Positions[b * 3 + 2] - mesh.Positions[a * 3 + 2];
                    double real = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (real <= 1e-12) continue;
                    yield return Math.Sqrt(du * du + dv * dv) / real;
                }
        }

        /// <summary>Whether this vertex sits exactly where one of the first
        /// `many` does.</summary>
        private static bool Somewhere(MeshDefinition mesh, int v, int many)
        {
            for (int other = 0; other < many; other++)
            {
                bool same = true;
                for (int k = 0; k < 3 && same; k++)
                    same = mesh.Positions[v * 3 + k] == mesh.Positions[other * 3 + k];
                if (same) return true;
            }
            return false;
        }

        private static void Near(double want, double got, double slack)
        {
            Assert.True(Math.Abs(want - got) <= slack,
                        "wanted " + want + ", got " + got);
        }
    }
}
