/*
Adapted from SW2URDF (https://github.com/ros/solidworks_urdf_exporter),
SW2URDF/Utilities/MathOPS.cs at commit c8b70b5069c69c290c83c95529052fc9f9e6ff63.
Copyright (c) 2015 Stephen Brawner, MIT licence. See
sw-addin\THIRD-PARTY-NOTICES.md and sw-addin\vendor\sw2urdf\LICENSE.

Changes from upstream: the MathNet.Numerics matrix type is replaced with plain
double[,] (row-major 4x4), and the SolidWorks MathTransform overloads moved to
Sw\SwFrames.cs so this file stays free of SolidWorks.Interop.*. The numeric
behaviour is unchanged and TestMathOps guards it.
*/

using System;

namespace Peak.SwToBlender.Core
{
    public static class MathOps
    {
        /// <summary>Values below this magnitude are treated as zero when
        /// cleaning axis vectors and origins for the manifest.</summary>
        public const double Epsilon = 1e-15;

        // ── Vectors ─────────────────────────────────────────────────────────

        public static double Dot(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        public static double[] Cross(double[] a, double[] b)
        {
            return new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0],
            };
        }

        public static double Norm(double[] a) => Math.Sqrt(Dot(a, a));

        /// <summary>Returns a normalised copy; a zero vector comes back as a
        /// zero vector rather than NaN.</summary>
        public static double[] Normalized(double[] a)
        {
            double n = Norm(a);
            var result = new double[a.Length];
            if (n < Epsilon) return result;
            for (int i = 0; i < a.Length; i++) result[i] = a[i] / n;
            return result;
        }

        public static double Distance2(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                sum += d * d;
            }
            return sum;
        }

        /// <summary>Zeroes every element whose magnitude is below minValue.
        /// Upstream used this to stop -1e-17 noise reaching the output file.</summary>
        public static double[] Threshold(double[] array, double minValue)
        {
            var result = (double[])array.Clone();
            for (int i = 0; i < array.Length; i++)
                result[i] = Math.Abs(array[i]) >= minValue ? array[i] : 0;
            return result;
        }

        // ── Lines ───────────────────────────────────────────────────────────

        public static double[] ClosestPointOnLineToPoint(double[] point, double[] line, double[] pointOnLine)
        {
            if (point.Length != line.Length || point.Length != pointOnLine.Length)
                throw new ArgumentException("Points and line vectors are not the same length");

            double denominator = 0;
            double numerator = 0;
            for (int i = 0; i < point.Length; i++)
            {
                denominator += line[i] * line[i];
                numerator += line[i] * (point[i] - pointOnLine[i]);
            }
            double k = numerator / denominator;
            var result = new double[point.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = pointOnLine[i] + k * line[i];
            return result;
        }

        /// <summary>
        /// A DOF probe reports an arbitrary point on the joint axis, sometimes
        /// far from the geometry. This slides it toward the child's bounding
        /// box: inside the box the point stays, outside it moves to whichever
        /// of the two box-corner projections is nearer. Upstream heuristic,
        /// kept as-is.
        /// </summary>
        public static double[] ClosestPointOnLineWithinBox(
            double xMin, double xMax, double yMin, double yMax, double zMin, double zMax,
            double[] line, double[] pointOnLine)
        {
            if (pointOnLine[0] > xMin && pointOnLine[0] < xMax &&
                pointOnLine[1] > yMin && pointOnLine[1] < yMax &&
                pointOnLine[2] > zMin && pointOnLine[2] < zMax)
            {
                return pointOnLine;
            }
            double[] point1 = ClosestPointOnLineToPoint(new[] { xMax, yMax, zMax }, line, pointOnLine);
            double[] point2 = ClosestPointOnLineToPoint(new[] { xMin, yMin, zMin }, line, pointOnLine);
            return Distance2(pointOnLine, point1) < Distance2(pointOnLine, point2) ? point1 : point2;
        }

        // ── 4x4 row-major transforms ────────────────────────────────────────

        public static double[,] Identity4()
        {
            var m = new double[4, 4];
            m[0, 0] = m[1, 1] = m[2, 2] = m[3, 3] = 1;
            return m;
        }

        public static double[,] Multiply(double[,] a, double[,] b)
        {
            var m = new double[4, 4];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) sum += a[i, k] * b[k, j];
                    m[i, j] = sum;
                }
            return m;
        }

        public static double[] GetXYZ(double[,] m) => new[] { m[0, 3], m[1, 3], m[2, 3] };

        /// <summary>Rotates a direction vector by the 3x3 part only.</summary>
        public static double[] RotateVector(double[,] m, double[] v)
        {
            var result = new double[3];
            for (int i = 0; i < 3; i++)
                result[i] = m[i, 0] * v[0] + m[i, 1] * v[1] + m[i, 2] * v[2];
            return result;
        }

        public static double[] TransformPoint(double[,] m, double[] p)
        {
            var result = RotateVector(m, p);
            result[0] += m[0, 3];
            result[1] += m[1, 3];
            result[2] += m[2, 3];
            return result;
        }

        /// <summary>Inverse of a rigid transform (orthonormal rotation +
        /// translation). Not a general matrix inverse.</summary>
        /// <summary>
        /// The mirror plane that takes placement <paramref name="a"/> to
        /// placement <paramref name="b"/>, or false when the two are not
        /// reflections of each other.
        ///
        /// Mirrored components carry no mate, so the relation has to be read
        /// off the placements themselves: S = B·A⁻¹ is a REFLECTION exactly
        /// when its linear part is symmetric, squares to the identity and
        /// has determinant −1. That is a real test, not a formality: a
        /// mirrored instance that has since been dragged somewhere else is
        /// no longer a reflection of its source, and treating it as one
        /// would invent a symmetry the model does not have.
        ///
        /// For a reflection L = I − 2nnᵀ, so (I − L)/2 is nnᵀ and the normal
        /// falls out of its largest diagonal: largest because a normal
        /// nearly perpendicular to an axis leaves that entry near zero and
        /// its sign unrecoverable.
        /// </summary>
        public static bool TryReflectionPlane(
            double[,] a, double[,] b, out double[] point, out double[] normal)
        {
            point = null;
            normal = null;
            if (a == null || b == null) return false;
            var s = Multiply(b, InvertRigid(a));

            // det of the linear part; a reflection is improper.
            double det =
                s[0, 0] * (s[1, 1] * s[2, 2] - s[1, 2] * s[2, 1])
                - s[0, 1] * (s[1, 0] * s[2, 2] - s[1, 2] * s[2, 0])
                + s[0, 2] * (s[1, 0] * s[2, 1] - s[1, 1] * s[2, 0]);
            if (det > -0.99 || det < -1.01) return false;
            for (int r = 0; r < 3; r++)
                for (int c = r + 1; c < 3; c++)
                    if (Math.Abs(s[r, c] - s[c, r]) > 1e-6) return false;

            var outer = new double[3, 3];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    outer[r, c] = ((r == c ? 1.0 : 0.0) - s[r, c]) * 0.5;

            int lead = 0;
            for (int i = 1; i < 3; i++) if (outer[i, i] > outer[lead, lead]) lead = i;
            if (outer[lead, lead] < 1e-9) return false;   // S is the identity
            double scale = Math.Sqrt(outer[lead, lead]);
            var n = new double[3];
            for (int i = 0; i < 3; i++) n[i] = outer[lead, i] / scale;
            n = Normalized(n);
            if (n == null) return false;

            // Confirm the reconstruction rather than trusting the algebra:
            // a rotation by 180 degrees is also symmetric and also squares
            // to the identity, and only differs here in its determinant.
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(s[r, c] - ((r == c ? 1.0 : 0.0) - 2.0 * n[r] * n[c])) > 1e-6)
                        return false;

            // The offset: S maps the origin to 2(p·n)n, a translation
            // purely ALONG the normal. Any component of it in the plane
            // makes S a glide reflection, which is not a mirror image of
            // anything: that is a mirrored instance somebody has since
            // dragged, and it has no symmetry left to model.
            double along = s[0, 3] * n[0] + s[1, 3] * n[1] + s[2, 3] * n[2];
            for (int i = 0; i < 3; i++)
                if (Math.Abs(s[i, 3] - along * n[i]) > 1e-6) return false;

            point = new[] { n[0] * along * 0.5, n[1] * along * 0.5, n[2] * along * 0.5 };
            normal = n;
            return true;
        }

        public static double[,] InvertRigid(double[,] m)
        {
            var inv = Identity4();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    inv[i, j] = m[j, i];
            var t = new[] { m[0, 3], m[1, 3], m[2, 3] };
            var rt = RotateVector(inv, t);
            inv[0, 3] = -rt[0];
            inv[1, 3] = -rt[1];
            inv[2, 3] = -rt[2];
            return inv;
        }

        /// <summary>
        /// Roll/pitch/yaw (x-y-z) from the rotation part, gimbal-lock guarded.
        /// This is upstream's GetRPY including the fix from their PR #171:
        /// keep the branch structure identical so TestMathOps stays a
        /// like-for-like port.
        /// </summary>
        public static double[] GetRPY(double[,] m)
        {
            double roll, pitch, yaw;
            if (Math.Abs(m[2, 0]) >= 1.0)
            {
                pitch = -Math.Asin(Math.Sign(m[2, 0]) * 1.0);
                roll = Math.Atan2(-m[1, 2], m[1, 1]);
                yaw = 0;
            }
            else
            {
                pitch = -Math.Asin(m[2, 0]);
                roll = Math.Atan2(m[2, 1], m[2, 2]);
                yaw = Math.Atan2(m[1, 0], m[0, 0]);
            }
            return new[] { roll, pitch, yaw };
        }

        public static double[,] GetRotation(double[] rpy)
        {
            double cr = Math.Cos(rpy[0]), sr = Math.Sin(rpy[0]);
            double cp = Math.Cos(rpy[1]), sp = Math.Sin(rpy[1]);
            double cy = Math.Cos(rpy[2]), sy = Math.Sin(rpy[2]);

            var rx = Identity4();
            rx[1, 1] = cr; rx[1, 2] = -sr; rx[2, 1] = sr; rx[2, 2] = cr;
            var ry = Identity4();
            ry[0, 0] = cp; ry[0, 2] = sp; ry[2, 0] = -sp; ry[2, 2] = cp;
            var rz = Identity4();
            rz[0, 0] = cy; rz[0, 1] = -sy; rz[1, 0] = sy; rz[1, 1] = cy;

            return Multiply(rz, Multiply(ry, rx));
        }

        public static double[,] GetTransformation(double[] xyz, double[] rpy)
        {
            var m = GetRotation(rpy);
            m[0, 3] = xyz[0];
            m[1, 3] = xyz[1];
            m[2, 3] = xyz[2];
            return m;
        }
    }
}
