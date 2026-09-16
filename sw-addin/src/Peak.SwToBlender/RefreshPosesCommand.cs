using Peak.SwToBlender.Bridge;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;

namespace Peak.SwToBlender
{
    /// <summary>
    /// "Refresh Poses": Blender moves its parts to where SolidWorks has
    /// them now.
    ///
    /// A send re-reads the mates, probes the freedom of every pair,
    /// tessellates and rebuilds the rig, which is minutes on a large
    /// assembly. Moving a part and dragging a mechanism changes none of
    /// that: only the transforms move. This walks the assembly, which is
    /// the cheap half of an export, and pushes the transforms. Blender
    /// puts them in the manifest it already holds, syncs the poses and
    /// puts the geometry back on its bones.
    ///
    /// It follows a send: the Blender scene must already hold the
    /// assembly, or there is no manifest to update, and Blender says so.
    /// Parts added or removed need a send, because the manifest describes
    /// the assembly as it was.
    /// </summary>
    public static class RefreshPosesCommand
    {
        public static void Run(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                app.SendMsgToUser2("Open an assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            try
            {
                var payload = Payload(model, assembly);
                int count = ((List<object>)
                    ((Dictionary<string, object>)payload["poses"])["components"]).Count;

                var instances = BlenderBridge.Discover(AddIn.Log);
                BlenderInstance target;
                if (instances.Count == 1) target = instances[0];
                else if (instances.Count > 1)
                {
                    target = InstanceChooserDialog.Choose(
                        ExportOptionsDialog.ActiveOwner(), instances);
                    if (target == null) return;
                }
                else
                {
                    app.SendMsgToUser2(
                        "No running Blender with the CAD Link bridge was found. "
                        + "Start Blender and send the assembly first.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                AddIn.Log("refresh poses: " + count + " component(s) to "
                          + "127.0.0.1:" + target.Port);
                var reply = BlenderBridge.PostImport(
                    target, payload, 5 * 60 * 1000, AddIn.Log);
                var settings = AppSettings.Load(AddIn.Log);
                bool ok = MiniJson.Flag(reply, "ok");
                if (ok && settings.FocusBlender) BlenderBridge.Focus(target, AddIn.Log);
                app.SendMsgToUser2(Summary(reply, count),
                    ok ? (int)swMessageBoxIcon_e.swMbInformation
                       : (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("refresh poses failed: " + ex);
                app.SendMsgToUser2("Refresh Poses failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        /// <summary>Where every component sits now. The same fields the
        /// listener's `poses` answer carries, so both routes reach Blender
        /// in one shape.</summary>
        internal static Dictionary<string, object> Payload(
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
            return new Dictionary<string, object>
            {
                { "source", "solidworks" },
                { "document", SafeTitle(model) },
                {
                    "poses", new Dictionary<string, object>
                    {
                        { "document", SafeTitle(model) },
                        { "components", components },
                    }
                },
            };
        }

        /// <summary>What to tell the user, from what Blender answered.</summary>
        internal static string Summary(Dictionary<string, object> reply, int sent)
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
    }
}
