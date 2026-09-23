using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// Where every component sits now, pushed to Blender: the cheap half of
    /// an export.
    ///
    /// A send re-reads the mates, probes the freedom of every pair,
    /// tessellates and rebuilds the rig, which is minutes on a large
    /// assembly. Moving a part and dragging a mechanism changes none of
    /// that: only the transforms move. This walks the assembly and pushes
    /// the transforms, and Blender puts them in the manifest it already
    /// holds.
    ///
    /// The ribbon does not offer it: parts added or removed need the whole
    /// export, and that is what Refresh Model is. Blender drives this from
    /// its own Update from CAD, and the lab harness uses it to drag a
    /// mechanism and see the result without paying for a send.
    /// </summary>
    public static class PosePush
    {
        /// <summary>The same fields the listener's `poses` answer carries,
        /// so both routes reach Blender in one shape.</summary>
        public static Dictionary<string, object> Payload(
            IModelDoc2 model, IAssemblyDoc assembly)
        {
            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            var components = new List<object>();
            foreach (var w in walked)
            {
                if (w.Graph == null) continue;
                components.Add(new Dictionary<string, object>
                {
                    { "id", w.Id },
                    { "sw_path", w.Graph.Path },
                    { "sw_persistent_id", ComponentIdentity.PersistIdBase64(model, w.Comp) },
                    { "transform", Flatten(w.Graph.Transform) },
                });
            }
            return PayloadOf(SafeTitle(model), SafePath(model), components);
        }

        /// <summary>The payload around the component rows. It names the
        /// document by its full path too, as a send does, so Blender can
        /// name it back in every request.</summary>
        internal static Dictionary<string, object> PayloadOf(
            string title, string documentPath, List<object> components)
        {
            var payload = new Dictionary<string, object>
            {
                { "source", "solidworks" },
                { "document", title },
                {
                    "poses", new Dictionary<string, object>
                    {
                        { "document", title },
                        { "components", components },
                    }
                },
            };
            if (!string.IsNullOrEmpty(documentPath))
                payload["source_document"] = documentPath;
            return payload;
        }

        /// <summary>What to tell the user, from what Blender answered. A
        /// push that moved nothing looks exactly like one that failed
        /// unless the message says which it was.</summary>
        public static string Summary(Dictionary<string, object> reply, int sent)
        {
            if (reply == null) return "Blender did not answer.";
            if (!MiniJson.Flag(reply, "ok"))
            {
                string error = MiniJson.Str(reply, "error", null);
                return string.IsNullOrEmpty(error)
                    ? "Blender refused the poses." : error;
            }
            var stages = MiniJson.Obj(reply, "stages");
            var poses = stages == null ? null : MiniJson.Obj(stages, "poses");
            int moved = poses == null ? 0 : (int)MiniJson.Num(poses, "moved", 0);
            if (moved == 0)
                return "Sent " + sent + " component pose(s). Every part in "
                     + "Blender was already where SolidWorks has it.";
            return "Sent " + sent + " component pose(s). Blender moved "
                 + moved + " part(s).";
        }

        private static double[] Flatten(double[,] t)
        {
            if (t == null) return null;
            var flat = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) flat[r * 4 + c] = t[r, c];
            return flat;
        }

        private static string SafeTitle(IModelDoc2 model)
        {
            try { return model.GetTitle(); }
            catch { return null; }
        }

        private static string SafePath(IModelDoc2 model)
        {
            try { return model.GetPathName(); }
            catch { return null; }
        }
    }
}
