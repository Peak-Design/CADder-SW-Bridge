using System;
using System.Collections.Generic;
using System.IO;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Persistent add-in settings: %APPDATA%\PeakDesign\CADder\settings.json.
    /// One flat object, read on every command and written when a dialog
    /// changes something, so two SolidWorks sessions cannot fight over stale
    /// in-memory copies. Missing file or unknown keys = defaults; the file
    /// never breaks an export.
    /// </summary>
    public sealed class AppSettings
    {
        // ── STEP appearance (the NEXT-STEP engine) ──────────────────────────
        public bool RepairAppearances = true;
        public bool DeInstance = true;
        public bool EngineeringMaterial = false;
        public bool IncludeHidden = false;
        public bool OnlySelected = false;

        // ── Rig export ──────────────────────────────────────────────────────
        public int Ap = 214;
        public bool RunDofProbe = true;
        /// <summary>Degrees per drag step when a cam or universal joint
        /// relation is read off the model. Smaller is finer and slower.</summary>
        public int RelationStepDeg = 5;
        public bool OpenFolder = true;

        // ── Blender import (forwarded over the bridge) ──────────────────────
        public string Hierarchy = "EMPTIES";       // FLAT|TREE|EMPTIES|COLLECTION_INSTANCES
        public string QualityPreset = "BALANCED";  // DRAFT|BALANCED|FINE|ULTRA|CUSTOM
        /// <summary>What Custom cuts to: the largest distance between the
        /// mesh and the true surface, in metres, and the largest angle one
        /// facet may turn through, in radians. The same two settings as the
        /// STEP import's Custom.</summary>
        public double QualityDistance = 0.0008;
        public double QualityAngle = 0.5;
        /// <summary>Cut each body to a share of its own size instead of a
        /// distance, with QualityAngle. The STEP import's Relative
        /// Tessellation, which measures each edge instead.</summary>
        public bool QualityRelative = false;
        public double QualityRelativeDistance = 0.005;
        /// <summary>The SolidWorks axis that becomes Blender's Z (the
        /// STEP importer's up_as spelling). SolidWorks models are Y up
        /// by convention, so YPOS turns them upright in Blender; ZPOS
        /// applies no rotation and keeps the manifest frame.</summary>
        public string UpAxis = "YPOS";
        public bool SeparateSolids = false;        // a multibody part per body
        /// <summary>Whether Blender pairs the tessellation triangles back
        /// into quads. The work is Blender's, and this is what tells it to
        /// do it, so a send and a rebuild in Blender give the same mesh.
        /// </summary>
        public bool TrisToQuads = true;
        /// <summary>Whether Blender unwraps the faces that no one scale can
        /// flatten: a sphere, a torus, a blend corner or a spline surface.
        /// A plane, a cylinder and a cone keep the exact chart of their own
        /// surface either way. The unwrap is Blender's, and this is what
        /// asks for it.</summary>
        public bool UnwrapCompound = true;
        /// <summary>Whether Blender turns its viewport to the angle this
        /// SolidWorks view is at once the parts arrive. Off by default: a
        /// send should not move a view somebody is working in.</summary>
        public bool MatchView = false;

        // ── The ribbon ──────────────────────────────────────────────────────
        /// <summary>When the add-in file this ribbon was built from was
        /// written. SolidWorks keeps the toolbar layout of a command group
        /// in the registry and reuses it, and a layout saved against an
        /// older build has shown the wrong caption on a button ("User
        /// Defined Route" on Send to Blender, Oscar, 2026-09-17). A build
        /// this has not seen throws the saved layout away once.</summary>
        public string CommandUiBuild = "";
        /// <summary>Free edges as POLY curves. The STEP route only: a
        /// direct send carries triangles, which have no free edges.
        /// </summary>
        public bool ImportCurves = false;

        // ── What a send carries of the SolidWorks appearances ───────────────
        public bool ExportAppearances = true;
        public bool ExportDecals = true;
        public bool ExportTextureMapping = true;

        // ── Bridge pipeline stages ──────────────────────────────────────────
        /// <summary>The rig is the point of the add-in, so this is the one
        /// stage worth turning off: geometry only, for a user who wants
        /// the parts and no bones. Syncing the poses, parenting the
        /// geometry and clearing the leftover empties are not options any
        /// more (Oscar, 2026-09-16): a rig that does not hold its geometry
        /// or does not sit on it is not a result anybody wants.</summary>
        public bool BuildRig = true;

        // ── Blender instance handling ───────────────────────────────────────
        public bool AutoLaunchBlender = true;
        public bool FocusBlender = true;
        /// <summary>Explicit blender.exe; empty = newest found install.</summary>
        public string BlenderExe = "";
        /// <summary>Where an export goes: "temp" for the app-data exports
        /// folder, "beside" for the folder the .SLDASM is in, "custom" for
        /// ExportFolder. Every one of them gets a folder of its own named
        /// after the document, so one export does not land on the last
        /// one's files (Oscar, 2026-09-16).</summary>
        public string ExportFolderMode = "temp";

        /// <summary>The folder "custom" writes into. Empty falls back to the
        /// app-data exports folder, which is what a user who picks the mode
        /// and cancels the browser gets.</summary>
        public string ExportFolder = "";

        /// <summary>What Refresh Model does with the rig: "APPEND" (add and
        /// remove bones, keeping the rest), "KEEP" or "REGENERATE". Asked
        /// each time and remembered, so a user who refreshes all day
        /// answers once.</summary>
        public string RigUpdateMode = "APPEND";

        // The lab: the add-in's localhost listener also accepts operations
        // that CHANGE the open model (open and close documents, suppress a
        // mate, set a dimension, rebuild, quit), so a test harness can drive
        // SolidWorks without the ribbon. Nothing is ever saved through it.
        // Off, and the listener answers only read requests. Off by default:
        // a user who installs the add-in did not ask for a localhost port
        // that can close their documents (2026-09-15). The lab turns it on
        // in settings.json or in the Export Options dialog.
        public bool LabOps = false;

        /// <summary>Whether this build carries the test harness at all.
        /// The harness drives SolidWorks over the localhost port, which is a
        /// tool for development and not a feature of the product, so a
        /// Release build shows no switch for it and the gate below refuses
        /// whatever a settings file asks for.</summary>
#if DEBUG
        public const bool LabBuild = true;
#else
        public const bool LabBuild = false;
#endif

        /// <summary>Whether the listener may run the operations that change
        /// the open model. A settings file written by a development build
        /// cannot turn them on in a shipped one.</summary>
        public bool LabOpsAllowed
        {
            get { return LabBuild && LabOps; }
        }

        /// <summary>Show the STEP route and the file exports on the
        /// ribbon. Off, the ribbon is Send to Blender and Export Options:
        /// the direct send is what nearly everyone needs. Read once when
        /// SolidWorks starts, because the command group is built then.</summary>
        public bool AdvancedCommands = false;

        public static string DefaultPath
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PeakDesign", "CADder", "settings.json");

        /// <summary>
        /// Where the settings were kept while the add-in was called
        /// SW To Blender. A user who had settings there keeps them: the
        /// first save writes the new file, and this one is never written
        /// again. Delete this once nobody is upgrading from that name.
        /// </summary>
        private static string RetiredPath
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Peak", "SwToBlender", "settings.json");

        public static AppSettings Load(Action<string> log = null, string path = null)
        {
            var settings = new AppSettings();
            bool asked = path != null;
            path = path ?? DefaultPath;
            try
            {
                if (!File.Exists(path) && !asked && File.Exists(RetiredPath))
                {
                    path = RetiredPath;
                    if (log != null) log("settings: reading the ones left by SW To Blender");
                }
                if (!File.Exists(path)) return settings;
                var obj = MiniJson.ParseObject(File.ReadAllText(path));
                settings.RepairAppearances = MiniJson.Flag(obj, "repair_appearances", settings.RepairAppearances);
                settings.DeInstance = MiniJson.Flag(obj, "de_instance", settings.DeInstance);
                settings.EngineeringMaterial = MiniJson.Flag(obj, "engineering_material", settings.EngineeringMaterial);
                settings.IncludeHidden = MiniJson.Flag(obj, "include_hidden", settings.IncludeHidden);
                settings.OnlySelected = MiniJson.Flag(obj, "only_selected", settings.OnlySelected);
                settings.ImportCurves = MiniJson.Flag(obj, "import_curves", settings.ImportCurves);
                settings.ExportAppearances = MiniJson.Flag(
                    obj, "export_appearances", settings.ExportAppearances);
                settings.ExportDecals = MiniJson.Flag(
                    obj, "export_decals", settings.ExportDecals);
                settings.ExportTextureMapping = MiniJson.Flag(
                    obj, "export_texture_mapping", settings.ExportTextureMapping);
                settings.SeparateSolids = MiniJson.Flag(obj, "separate_solids", settings.SeparateSolids);
                settings.TrisToQuads = MiniJson.Flag(obj, "tris_to_quads", settings.TrisToQuads);
                settings.UnwrapCompound = MiniJson.Flag(obj, "unwrap_compound", settings.UnwrapCompound);
                settings.MatchView = MiniJson.Flag(obj, "match_view", settings.MatchView);
                settings.CommandUiBuild = MiniJson.Str(obj, "command_ui_build", settings.CommandUiBuild);
                settings.Ap = MiniJson.Int(obj, "ap", settings.Ap);
                settings.RunDofProbe = MiniJson.Flag(obj, "run_dof_probe", settings.RunDofProbe);
                settings.RelationStepDeg = MiniJson.Int(obj, "relation_step_deg", settings.RelationStepDeg);
                settings.OpenFolder = MiniJson.Flag(obj, "open_folder", settings.OpenFolder);
                settings.Hierarchy = MiniJson.Str(obj, "hierarchy", settings.Hierarchy);
                settings.QualityPreset = MiniJson.Str(obj, "quality_preset", settings.QualityPreset);
                settings.QualityDistance = MiniJson.Num(obj, "quality_distance_m", settings.QualityDistance);
                settings.QualityAngle = MiniJson.Num(obj, "quality_angle_rad", settings.QualityAngle);
                settings.QualityRelative = MiniJson.Flag(obj, "quality_relative", settings.QualityRelative);
                settings.QualityRelativeDistance = MiniJson.Num(obj, "quality_relative_distance", settings.QualityRelativeDistance);
                settings.UpAxis = MiniJson.Str(obj, "up_axis", settings.UpAxis);
                settings.BuildRig = MiniJson.Flag(obj, "build_rig", settings.BuildRig);
                settings.AutoLaunchBlender = MiniJson.Flag(obj, "auto_launch_blender", settings.AutoLaunchBlender);
                settings.FocusBlender = MiniJson.Flag(obj, "focus_blender", settings.FocusBlender);
                settings.BlenderExe = MiniJson.Str(obj, "blender_exe", settings.BlenderExe);
                settings.ExportFolderMode = MiniJson.Str(obj, "export_folder_mode", settings.ExportFolderMode);
                settings.ExportFolder = MiniJson.Str(obj, "export_folder", settings.ExportFolder);
                settings.RigUpdateMode = MiniJson.Str(obj, "rig_update_mode", settings.RigUpdateMode);
                settings.LabOps = MiniJson.Flag(obj, "lab_ops", settings.LabOps);
                settings.AdvancedCommands = MiniJson.Flag(obj, "advanced_commands", settings.AdvancedCommands);
            }
            catch (Exception ex)
            {
                if (log != null) log("settings load failed, using defaults: " + ex.Message);
            }
            return settings;
        }

        public void Save(Action<string> log = null, string path = null)
        {
            path = path ?? DefaultPath;
            try
            {
                var obj = new Dictionary<string, object>
                {
                    { "repair_appearances", RepairAppearances },
                    { "de_instance", DeInstance },
                    { "engineering_material", EngineeringMaterial },
                    { "include_hidden", IncludeHidden },
                    { "only_selected", OnlySelected },
                    { "import_curves", ImportCurves },
                    { "separate_solids", SeparateSolids },
                    { "tris_to_quads", TrisToQuads },
                    { "unwrap_compound", UnwrapCompound },
                    { "match_view", MatchView },
                    { "command_ui_build", CommandUiBuild },
                    { "export_appearances", ExportAppearances },
                    { "export_decals", ExportDecals },
                    { "export_texture_mapping", ExportTextureMapping },
                    { "ap", Ap },
                    { "run_dof_probe", RunDofProbe },
                    { "relation_step_deg", RelationStepDeg },
                    { "open_folder", OpenFolder },
                    { "hierarchy", Hierarchy },
                    { "quality_preset", QualityPreset },
                    { "quality_distance_m", QualityDistance },
                    { "quality_angle_rad", QualityAngle },
                    { "quality_relative", QualityRelative },
                    { "quality_relative_distance", QualityRelativeDistance },
                    { "up_axis", UpAxis },
                    { "build_rig", BuildRig },
                    { "auto_launch_blender", AutoLaunchBlender },
                    { "focus_blender", FocusBlender },
                    { "blender_exe", BlenderExe ?? "" },
                    { "export_folder_mode", ExportFolderMode },
                    { "export_folder", ExportFolder ?? "" },
                    { "rig_update_mode", RigUpdateMode ?? "APPEND" },
                    { "lab_ops", LabOps },
                    { "advanced_commands", AdvancedCommands },
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // UTF-8 without BOM, like every JSON this add-in writes.
                File.WriteAllText(path, MiniJson.Write(obj),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (log != null) log("settings save failed: " + ex.Message);
            }
        }
    }
}
