using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Where SolidWorks is looking from, so Blender can look from there too.
    ///
    /// The graphics view reports its orientation as a transform from model
    /// space to view space. Its three columns are the axes of the screen:
    /// across, up, and out of the screen towards the viewer. That is all a
    /// consumer needs to stand at the same angle, and it is reported the
    /// same way to the screenshot command, which is how the render
    /// comparisons of 2026-09-15 stood a camera where SolidWorks stood.
    ///
    /// The box comes with it so the consumer can frame the model rather
    /// than guess a distance. What is NOT here is the pan and the zoom: the
    /// consumer frames what it has, which is what a person expects to see
    /// after an import.
    /// </summary>
    public static class ViewReader
    {
        /// <summary>The active view, or null when there is none to read.
        /// Never throws: a view that cannot be read is a view the consumer
        /// does without.</summary>
        public static Dictionary<string, object> Read(IModelDoc2 model)
        {
            if (model == null) return null;
            try
            {
                var view = model.ActiveView as IModelView;
                if (view == null) return null;
                var orientation = ((IMathTransform)view.Orientation3).ArrayData
                    as double[];
                if (orientation == null || orientation.Length < 9) return null;
                var reply = new Dictionary<string, object>
                {
                    { "orientation", orientation },
                    { "scale", view.Scale2 },
                };
                var box = Box(model);
                if (box != null) reply["box"] = box;
                return reply;
            }
            catch (Exception ex)
            {
                AddIn.Log("view: " + ex.Message);
                return null;
            }
        }

        /// <summary>The model's own bounding box, in metres.</summary>
        private static double[] Box(IModelDoc2 model)
        {
            try
            {
                var part = model as IPartDoc;
                if (part != null) return part.GetPartBox(true) as double[];
                var assembly = model as IAssemblyDoc;
                if (assembly != null)
                    return assembly.GetBox(
                        (int)swBoundingBoxOptions_e.swBoundingBoxIncludeRefPlanes)
                        as double[];
            }
            catch (Exception) { }
            return null;
        }
    }
}
