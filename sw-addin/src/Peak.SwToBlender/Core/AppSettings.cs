using System;
using System.Collections.Generic;
using System.IO;

namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// Persistent add-in settings — %APPDATA%\Peak\SwToBlender\settings.json.
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

        // ── Rig export ──────────────────────────────────────────────────────
        public int Ap = 214;
        public bool RunDofProbe = true;
        public bool OpenFolder = true;

        // ── Blender import (forwarded over the bridge) ──────────────────────
        public string Hierarchy = "EMPTIES";       // FLAT|TREE|EMPTIES|COLLECTION_INSTANCES
        public string QualityPreset = "BALANCED";  // DRAFT|BALANCED|FINE|ULTRA
        public string UpAxis = "ZPOS";             // XPOS|YPOS|ZPOS — ZPOS keeps the manifest frame

        // ── Bridge pipeline stages ──────────────────────────────────────────
        public bool BuildRig = true;
        public bool SyncPoses = true;
        public bool ParentGeometry = true;
        public bool CleanupEmpties = true;

        // ── Blender instance handling ───────────────────────────────────────
        public bool AutoLaunchBlender = true;
        public bool FocusBlender = true;
        /// <summary>Explicit blender.exe; empty = newest found install.</summary>
        public string BlenderExe = "";
        /// <summary>"temp" exports into a per-assembly folder under
        /// %LOCALAPPDATA%; "beside" writes next to the .SLDASM.</summary>
        public string ExportFolderMode = "temp";

        public static string DefaultPath
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Peak", "SwToBlender", "settings.json");

        public static AppSettings Load(Action<string> log = null, string path = null)
        {
            var settings = new AppSettings();
            path = path ?? DefaultPath;
            try
            {
                if (!File.Exists(path)) return settings;
                var obj = MiniJson.ParseObject(File.ReadAllText(path));
                settings.RepairAppearances = MiniJson.Flag(obj, "repair_appearances", settings.RepairAppearances);
                settings.DeInstance = MiniJson.Flag(obj, "de_instance", settings.DeInstance);
                settings.EngineeringMaterial = MiniJson.Flag(obj, "engineering_material", settings.EngineeringMaterial);
                settings.IncludeHidden = MiniJson.Flag(obj, "include_hidden", settings.IncludeHidden);
                settings.Ap = MiniJson.Int(obj, "ap", settings.Ap);
                settings.RunDofProbe = MiniJson.Flag(obj, "run_dof_probe", settings.RunDofProbe);
                settings.OpenFolder = MiniJson.Flag(obj, "open_folder", settings.OpenFolder);
                settings.Hierarchy = MiniJson.Str(obj, "hierarchy", settings.Hierarchy);
                settings.QualityPreset = MiniJson.Str(obj, "quality_preset", settings.QualityPreset);
                settings.UpAxis = MiniJson.Str(obj, "up_axis", settings.UpAxis);
                settings.BuildRig = MiniJson.Flag(obj, "build_rig", settings.BuildRig);
                settings.SyncPoses = MiniJson.Flag(obj, "sync_poses", settings.SyncPoses);
                settings.ParentGeometry = MiniJson.Flag(obj, "parent_geometry", settings.ParentGeometry);
                settings.CleanupEmpties = MiniJson.Flag(obj, "cleanup_empties", settings.CleanupEmpties);
                settings.AutoLaunchBlender = MiniJson.Flag(obj, "auto_launch_blender", settings.AutoLaunchBlender);
                settings.FocusBlender = MiniJson.Flag(obj, "focus_blender", settings.FocusBlender);
                settings.BlenderExe = MiniJson.Str(obj, "blender_exe", settings.BlenderExe);
                settings.ExportFolderMode = MiniJson.Str(obj, "export_folder_mode", settings.ExportFolderMode);
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
                    { "ap", Ap },
                    { "run_dof_probe", RunDofProbe },
                    { "open_folder", OpenFolder },
                    { "hierarchy", Hierarchy },
                    { "quality_preset", QualityPreset },
                    { "up_axis", UpAxis },
                    { "build_rig", BuildRig },
                    { "sync_poses", SyncPoses },
                    { "parent_geometry", ParentGeometry },
                    { "cleanup_empties", CleanupEmpties },
                    { "auto_launch_blender", AutoLaunchBlender },
                    { "focus_blender", FocusBlender },
                    { "blender_exe", BlenderExe ?? "" },
                    { "export_folder_mode", ExportFolderMode },
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
