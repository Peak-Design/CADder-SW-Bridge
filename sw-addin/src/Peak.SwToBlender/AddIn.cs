using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Peak.SwToBlender
{
    // Shell cloned from Peak.NextStep\AddIn.cs — the traps it documents
    // (registry casing, tab rebuild, icon paths, interop binding) apply here
    // unchanged. Only the identity, the command set and the callbacks differ.
    [ComVisible(true)]
    [Guid("5A19BED7-5BAE-4520-A820-99C7466C42AC")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(ISwAddin))]
    public class AddIn : ISwAddin
    {
        public const string AddInTitle = "SW To Blender";

        /// <summary>
        /// This code reads the version from the assembly. Nobody writes it
        /// twice. The csproj &lt;Version&gt; is the one source. A release
        /// therefore cannot ship a binary whose registry entry claims a
        /// different version.
        /// </summary>
        public static string AddInVersion =>
            "v" + (typeof(AddIn).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        /// <summary>
        /// The titles that this add-in has used before. SolidWorks finds a
        /// command tab by its title. A new title therefore abandons the old tab,
        /// which stays in the SolidWorks layout of the user with a dead button
        /// on it. This code removes those tabs at every connect.
        /// </summary>
        private static readonly string[] RetiredTitles = { };

        public const string AddInDescription =
            "Exports the assembly's kinematics as a rig manifest next to a "
            + "STEP file, for the sw_to_blender Blender add-on.";

        public static ISldWorks SwApp { get; private set; }

        private int _cookie;
        private ICommandManager _cmdMgr;
        private CommandCallbacks _callbacks;

        /// <summary>The CommandGroup UserID. SolidWorks keeps it in the registry
        /// with the toolbar layout of the user, so it must never change.
        /// 71 is NEXT-STEP, 74 is this add-in.</summary>
        private const int MainCmdGroupId = 74;
        private const int CmdExportUserId = 0;
        private const int CmdStepPlusUserId = 1;
        private const int CmdSendUserId = 2;
        private const int CmdOptionsUserId = 3;
        private const int CmdExportJsonUserId = 4;

        // ── Cross-version interop resolver ──────────────────────────────────
        // This add-in compiles against the oldest installed interops. A newer
        // SolidWorks loads it, and the CLR then cannot find that exact assembly
        // version. This handler points at the interops of the SolidWorks that
        // is running. It is only a fallback. The Private=True copies next to the
        // DLL satisfy the bind that happens while the AddIn type itself loads.
        static AddIn()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var name = new AssemblyName(args.Name);
                if (!name.Name.StartsWith("SolidWorks.Interop.", StringComparison.OrdinalIgnoreCase))
                    return null;
                try
                {
                    string swDir = Path.GetDirectoryName(
                        System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                    string dll = Path.Combine(swDir ?? "", name.Name + ".dll");
                    if (File.Exists(dll)) return Assembly.LoadFrom(dll);
                }
                catch { }
                return null;
            };
        }

        // ── Registration ────────────────────────────────────────────────────

        /// <summary>
        /// The keys of the installed SolidWorks versions, under
        /// HKLM\SOFTWARE\SolidWorks.
        ///
        /// Two traps, found on this machine:
        ///   * The case is NOT consistent. 2022, 2024 and 2025 use "SOLIDWORKS
        ///     &lt;year&gt;", but 2026 uses "SolidWorks 2026". A case sensitive
        ///     StartsWith skips 2026 without a message, and the add-in never
        ///     appears.
        ///   * "SOLIDWORKS CAM" also starts with "SOLIDWORKS " and is not a
        ///     SolidWorks version. A loose filter registers the add-in into it.
        ///
        /// A 4-digit year after the prefix prevents both faults.
        /// </summary>
        private static IEnumerable<string> InstalledVersionKeys(RegistryKey swKey)
        {
            foreach (var name in swKey.GetSubKeyNames())
            {
                if (!name.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase))
                    continue;
                var suffix = name.Substring("SOLIDWORKS ".Length).Trim();
                if (suffix.Length == 4 && suffix.All(char.IsDigit))
                    yield return name;
            }
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            var guid = t.GUID.ToString("B");
            using (var swKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (swKey == null) return;
                foreach (var ver in InstalledVersionKeys(swKey))
                {
                    using (var key = Registry.LocalMachine.CreateSubKey(
                        $@"SOFTWARE\SolidWorks\{ver}\Addins\{guid}"))
                    {
                        key.SetValue(null, 1);
                        key.SetValue("Title", AddInTitle + " " + AddInVersion);
                        key.SetValue("Description", AddInDescription);
                    }
                }
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            var guid = t.GUID.ToString("B");
            using (var swKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
            {
                if (swKey == null) return;
                // Unregister removes every key that starts with the prefix, not
                // only the keys with a year. An earlier and looser filter could
                // have written a key in the wrong place, such as under
                // "SOLIDWORKS CAM". This removes that key instead of leaving it.
                foreach (var ver in swKey.GetSubKeyNames()
                             .Where(n => n.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase)))
                    Registry.LocalMachine.DeleteSubKey(
                        $@"SOFTWARE\SolidWorks\{ver}\Addins\{guid}", throwOnMissingSubKey: false);
            }
        }

        // ── ISwAddin ────────────────────────────────────────────────────────
        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                SwApp = (ISldWorks)ThisSW;
                _cookie = Cookie;

                _callbacks = new CommandCallbacks { Owner = this };
                SwApp.SetAddinCallbackInfo2(0, _callbacks, _cookie);
                _cmdMgr = SwApp.GetCommandManager(_cookie);
                BuildCommandUI();

                Log("connected");
                return true;
            }
            catch (Exception ex)
            {
                Log("ConnectToSW failed: " + ex);
                return false;
            }
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (_cmdMgr != null)
                {
                    _cmdMgr.RemoveCommandGroup2(MainCmdGroupId, true);
                    Marshal.ReleaseComObject(_cmdMgr);
                    _cmdMgr = null;
                }
            }
            catch (Exception ex) { Log("DisconnectFromSW: " + ex.Message); }

            SwApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private void BuildCommandUI()
        {
            int errors = 0;

            // Discard the saved toolbar layout of the user only when the command
            // set changes. A value of ignorePrevious:true on every load throws
            // away the layout each time.
            object registryIds;
            bool hadPrevious = _cmdMgr.GetGroupDataFromRegistry(MainCmdGroupId, out registryIds);
            var knownIds = new[] { CmdExportUserId, CmdStepPlusUserId, CmdSendUserId,
                CmdOptionsUserId, CmdExportJsonUserId };
            bool ignorePrevious = hadPrevious && !SameIds(registryIds as int[], knownIds);

            var group = _cmdMgr.CreateCommandGroup2(
                MainCmdGroupId, AddInTitle, AddInDescription, "", -1, ignorePrevious, ref errors);
            if (group == null) { Log($"CreateCommandGroup2 failed ({errors})"); return; }

            ApplyIcons(group);

            group.AddCommandItem2(
                "Send to Blender", -1,
                "Export and import straight into a running Blender: geometry, "
                + "appearances, rig, parenting — one click, options behind Blender Options",
                "Send to Blender", 0,
                nameof(CommandCallbacks.SendToBlender),
                nameof(CommandCallbacks.EnableAnyDoc),
                CmdSendUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.AddCommandItem2(
                "Export Rig + STEP", -1,
                "Export the assembly's kinematics as a rig manifest next to a STEP file",
                // Image index 0 for every command: the icon strips carry ONE
                // icon per size, so any other index would point past the strip
                // and silently break the artwork.
                "Export Rig + STEP", 0,
                nameof(CommandCallbacks.ExportRig),
                nameof(CommandCallbacks.EnableExportRig),
                CmdExportUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.AddCommandItem2(
                "Export Rig JSON", -1,
                "Re-export only the rig manifest — no STEP write. For iterating on "
                + "large assemblies: occurrences are matched against the existing "
                + "STEP file beside the manifest when there is one",
                "Export Rig JSON", 0,
                nameof(CommandCallbacks.ExportRigJson),
                nameof(CommandCallbacks.EnableExportRig),
                CmdExportJsonUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.AddCommandItem2(
                "Export STEP+", -1,
                "Export STEP and keep the full appearance hierarchy (the NEXT-STEP engine)",
                "Export STEP+", 0,
                nameof(CommandCallbacks.ExportStepPlus),
                nameof(CommandCallbacks.EnableAnyDoc),
                CmdStepPlusUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.AddCommandItem2(
                "Blender Options", -1,
                "Persistent options: Blender import, rig pipeline, appearances, "
                + "which Blender to use",
                "Blender Options", 0,
                nameof(CommandCallbacks.BlenderOptions),
                nameof(CommandCallbacks.EnableAlways),
                CmdOptionsUserId,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));

            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();

            // Parts get the appearance/bridge commands; the rig export needs
            // mates and stays assembly-only.
            //
            // SolidWorks keeps the tab between sessions. A call to
            // AddCommandTabBox() and AddCommands() on every launch therefore
            // adds ANOTHER copy of the button each time. This code removes the
            // existing tab first and builds it again. The SolidWorks samples use
            // the same method, because no reliable call asks a tab box whether
            // it already holds a command.
            foreach (var docType in new[] { swDocumentTypes_e.swDocASSEMBLY, swDocumentTypes_e.swDocPART })
            {
                foreach (var title in new[] { AddInTitle }.Concat(RetiredTitles))
                {
                    var stale = _cmdMgr.GetCommandTab((int)docType, title);
                    if (stale != null) _cmdMgr.RemoveCommandTab(stale);
                }

                var tab = _cmdMgr.AddCommandTab((int)docType, AddInTitle);
                if (tab == null) { Log($"AddCommandTab failed for docType {docType}"); continue; }

                var box = tab.AddCommandTabBox();
                if (box == null) { Log($"AddCommandTabBox failed for docType {docType}"); continue; }

                var userIds = docType == swDocumentTypes_e.swDocASSEMBLY
                    ? new[] { CmdSendUserId, CmdExportUserId, CmdExportJsonUserId,
                              CmdStepPlusUserId, CmdOptionsUserId }
                    : new[] { CmdSendUserId, CmdStepPlusUserId, CmdOptionsUserId };
                var commandIds = userIds.Select(id => group.get_CommandID(id)).ToArray();
                var styles = userIds.Select(
                    _ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
                bool added = box.AddCommands(commandIds, styles);
                if (!added) Log($"AddCommands failed for docType {docType}");
            }
        }

        /// <summary>The icon sizes that SolidWorks asks for, smallest first.</summary>
        private static readonly int[] IconSizes = { 20, 32, 40, 64, 96, 128 };

        /// <summary>
        /// Points the command group at the PNG icons next to the DLL.
        ///
        /// SolidWorks takes ABSOLUTE paths and reads the files only when it
        /// needs them. A missing file therefore gives no error, no icon and no
        /// explanation. This code examines every path first. If a file is
        /// missing, it writes a log entry and continues. The button then keeps
        /// the default SolidWorks artwork, and the add-in still loads.
        /// </summary>
        private void ApplyIcons(ICommandGroup group)
        {
            try
            {
                string dir = Path.Combine(
                    Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? ".", "icons");

                var commands = IconSizes.Select(s => Path.Combine(dir, $"SwToBlender_{s}.png")).ToArray();
                var main = IconSizes.Select(s => Path.Combine(dir, $"SwToBlenderMain_{s}.png")).ToArray();

                var missing = commands.Concat(main).Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0)
                {
                    Log($"icons not found ({missing.Count} missing, e.g. {missing[0]}); "
                      + "using SolidWorks defaults");
                    return;
                }

                group.IconList = commands;
                group.MainIconList = main;
            }
            catch (Exception ex) { Log("ApplyIcons: " + ex.Message); }
        }

        private static bool SameIds(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ── Logging ─────────────────────────────────────────────────────────
        public static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? ".";
                // The name needs its namespace. SolidWorks.Interop.sldworks
                // also declares an Environment type, so the short name is
                // ambiguous here.
                File.AppendAllText(Path.Combine(dir, "swtoblender-debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message
                    + System.Environment.NewLine);
            }
            catch { /* a log failure must never stop the add-in */ }
        }
    }

    /// <summary>
    /// SolidWorks sends the ribbon callbacks by method name, through late
    /// binding. AddIn uses ClassInterface(None), so it exposes only ISwAddin.
    /// This separate AutoDispatch object receives the callbacks.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class CommandCallbacks
    {
        public AddIn Owner { get; set; }

        public void ExportRig() => ExportCommand.Run(AddIn.SwApp);
        public void ExportRigJson() => ExportCommand.RunManifestOnly(AddIn.SwApp);
        public void ExportStepPlus() => StepPlusCommand.Run(AddIn.SwApp);
        public void SendToBlender() => SendToBlenderCommand.Run(AddIn.SwApp);
        public void BlenderOptions() => BlenderOptionsDialog.Run(AddIn.SwApp);

        /// <summary>1 enables the button. 0 makes it grey.</summary>
        public int EnableExportRig()
        {
            var doc = AddIn.SwApp?.ActiveDoc as IModelDoc2;
            if (doc == null) return 0;
            return doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
        }

        public int EnableAnyDoc()
        {
            var doc = AddIn.SwApp?.ActiveDoc as IModelDoc2;
            if (doc == null) return 0;
            int type = doc.GetType();
            return type == (int)swDocumentTypes_e.swDocASSEMBLY
                || type == (int)swDocumentTypes_e.swDocPART ? 1 : 0;
        }

        public int EnableAlways() => 1;
    }
}
