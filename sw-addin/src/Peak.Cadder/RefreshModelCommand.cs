using System;
using System.Collections.Generic;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder
{
    /// <summary>
    /// "Refresh Model": bring the Blender scene up to date with the
    /// assembly as it is now.
    ///
    /// A send replaces the import, which is right the first time and wrong
    /// every time after it: everything done in Blender since goes with it.
    /// This sends the same export and asks Blender to UPDATE instead:
    /// parts that are still there keep their objects, their materials and
    /// their modifiers, parts that are new arrive, parts that have gone are
    /// removed, and everything moves to where SolidWorks has it now.
    ///
    /// The rig is the one thing that cannot be decided here, so the user is
    /// asked (RigUpdateDialog): keep it as it is, rebuild it inside the
    /// armature that is there so an animation survives, or build a new one.
    ///
    /// It follows a send: the Blender scene must already hold this
    /// assembly. The ribbon greys the button until a running Blender with
    /// the bridge holds a scene of this document (BlenderBridge.AnyHolding),
    /// and the refresh goes to that Blender.
    /// </summary>
    public static class RefreshModelCommand
    {
        public static void Run(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            if (model == null)
            {
                app.SendMsgToUser2("Open a part or assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }
            // The rule the ribbon greys the button by, for a click that
            // came before the ribbon caught up.
            string path = model.GetPathName();
            if (!BlenderBridge.AnyHolding(path, AddIn.WasSent(path)))
            {
                app.SendMsgToUser2(
                    "This document is not open in a running Blender. Send it "
                    + "to Blender first. A refresh brings a scene up to date, "
                    + "and there is nothing to bring up to date until it has "
                    + "been sent.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            string mode = RigUpdateDialog.Choose(
                ExportOptionsDialog.ActiveOwner(), settings,
                model as IAssemblyDoc != null);
            if (mode == null) return;       // the user cancelled
            settings.RigUpdateMode = mode;
            settings.Save(AddIn.Log);

            AddIn.Log("refresh model: rig mode " + mode);
            SendToBlenderCommand.Run(app, native: true, update: true, rigMode: mode);
        }

        /// <summary>What to tell the user, from what Blender answered. A
        /// refresh that changed nothing looks exactly like one that failed
        /// unless the message says which it was.</summary>
        internal static string Summary(Dictionary<string, object> reply)
        {
            if (reply == null) return "Blender did not answer.";
            if (!MiniJson.Flag(reply, "ok"))
            {
                string error = MiniJson.Str(reply, "error", null);
                return string.IsNullOrEmpty(error)
                    ? "Blender refused the refresh." : error;
            }
            var stages = MiniJson.Obj(reply, "stages");
            var update = stages == null ? null : MiniJson.Obj(stages, "update");
            if (update == null)
                return "Blender took the assembly, and reported no changes.";
            int added = Count(update, "added");
            int removed = Count(update, "removed");
            int moved = Count(update, "moved");
            int reshaped = Count(update, "reshaped");
            // Parts with new geometry that kept their own, because Lock
            // Geometry is on for them in Blender. Without the count, a
            // refresh where only a locked part changed said nothing had.
            int locked = Count(update, "locked");
            int kept = (int)MiniJson.Num(update, "kept", 0);
            if (added + removed + moved + reshaped + locked == 0)
                return "Nothing has changed since the last send. "
                     + kept + " part(s) left as they are.";
            var said = new List<string>();
            if (added > 0) said.Add(added + " added");
            if (removed > 0) said.Add(removed + " removed");
            if (moved > 0) said.Add(moved + " moved");
            if (reshaped > 0) said.Add(reshaped + " re-tessellated");
            if (locked > 0) said.Add(locked + " kept their locked geometry");
            return "Blender is up to date: " + string.Join(", ", said.ToArray())
                 + ", " + kept + " unchanged." + RigLine(stages);
        }

        private static string RigLine(Dictionary<string, object> stages)
        {
            var rig = MiniJson.Obj(stages, "rig");
            if (rig == null) return "";
            string locked = MiniJson.Str(rig, "locked", null);
            if (!string.IsNullOrEmpty(locked))
                return " The rig " + locked + " is locked and was left alone.";
            string mode = MiniJson.Str(rig, "mode", null);
            if (mode == "KEEP") return " The rig was left as it is.";
            if (mode == "REGENERATE") return " The rig was built again.";
            if (mode == "APPEND")
            {
                int added = Count(rig, "added");
                int removed = Count(rig, "removed");
                if (added + removed == 0)
                    return " The rig needed no new bones.";
                return " The rig gained " + added + " bone(s) and lost "
                     + removed + ".";
            }
            return "";
        }

        private static int Count(Dictionary<string, object> obj, string key)
        {
            var list = MiniJson.Arr(obj, key);
            return list == null ? 0 : list.Count;
        }
    }
}
