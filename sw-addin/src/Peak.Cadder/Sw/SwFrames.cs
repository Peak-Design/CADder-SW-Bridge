using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// MathTransform to double[4,4] conversion, and the lifts from a component
    /// frame into the assembly's global frame. Everything downstream of this
    /// file works on plain row-major column-vector matrices; SolidWorks
    /// interop types stop here.
    /// </summary>
    public static class SwFrames
    {
        /// <summary>
        /// Converts IMathTransform.ArrayData to the manifest's 4x4.
        ///
        /// The layout, from the API help (sldworksapi/
        /// SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.
        /// IMathTransform~ArrayData.html): elements 0..8 are the 3x3 rotation,
        /// 9..11 the translation, 12 the scale, 13..15 unused.
        ///
        /// SolidWorks stores the rotation for ROW vectors (p' = p·M + t). The
        /// manifest uses column vectors (p' = R·p + t), so R is the transpose:
        /// m[i,j] = data[i + 3*j]. SW2URDF reads the array the same way
        /// (vendor/sw2urdf/MathOps.cs, GetRotationMatrix / GetTransformation),
        /// which is the cross-check that the transpose goes this way round and
        /// not the other.
        ///
        /// The scale element is 1.0 for every component transform SolidWorks
        /// produces, but it is folded into the rotation anyway so a scaled
        /// transform cannot silently shrink every mate point.
        /// </summary>
        public static double[,] ToMatrix(MathTransform transform)
        {
            if (transform == null) return null;
            var data = transform.ArrayData as double[];
            if (data == null || data.Length < 12) return null;

            double scale = data.Length > 12 && data[12] != 0 ? data[12] : 1.0;
            var m = MathOps.Identity4();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] = data[i + 3 * j] * scale;
            m[0, 3] = data[9];
            m[1, 3] = data[10];
            m[2, 3] = data[11];
            return m;
        }

        /// <summary>
        /// The component's placement in the assembly's global frame.
        ///
        /// IComponent2.Transform2 is root-relative at every depth: "You must
        /// specify the transform in relation to the root component" (API help,
        /// IComponent2~Transform2.html), so a child of a nested subassembly
        /// needs no accumulation up the tree. Null for components whose
        /// transform SolidWorks does not hold (suppressed occurrences).
        /// </summary>
        public static double[,] ComponentWorld(IComponent2 component)
        {
            if (component == null) return null;
            try { return ToMatrix(component.Transform2); }
            catch { return null; }
        }

        /// <summary>The converse of ToMatrix: a manifest-convention 4x4 back
        /// into a SolidWorks MathTransform (rotation transposed into the row
        /// vector layout, unit scale). Null when the utility or matrix is
        /// missing.</summary>
        public static MathTransform FromMatrix(IMathUtility mathUtil, double[,] m)
        {
            if (mathUtil == null || m == null) return null;
            var data = new double[16];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    data[i + 3 * j] = m[i, j];
            data[9] = m[0, 3];
            data[10] = m[1, 3];
            data[11] = m[2, 3];
            data[12] = 1.0;
            return mathUtil.CreateTransform(data) as MathTransform;
        }

        /// <summary>Lifts a point from a component/subassembly frame to
        /// global. A null frame means the point already is global.</summary>
        public static double[] LiftPoint(double[,] frame, double[] point)
        {
            if (point == null) return null;
            if (frame == null) return (double[])point.Clone();
            return MathOps.TransformPoint(frame, point);
        }

        /// <summary>Lifts a direction (rotation only, no translation) and
        /// re-normalises, because a folded-in scale would otherwise stretch
        /// the unit vector.</summary>
        public static double[] LiftDirection(double[,] frame, double[] direction)
        {
            if (direction == null) return null;
            if (frame == null) return MathOps.Normalized(direction);
            return MathOps.Normalized(MathOps.RotateVector(frame, direction));
        }
    }
}
