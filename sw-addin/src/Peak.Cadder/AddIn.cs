using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using System;
using Peak.Cadder.Core;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

// No class is visible to COM unless it says so. Setup runs RegAsm
// /codebase, which registers every public class of a COM-visible assembly
// machine-wide: 70 of them, the dialogs among them. SolidWorks needs two,
// AddIn and CommandCallbacks, and both carry [ComVisible(true)]. The csproj
// property ComVisible did nothing: the SDK has no such property.
[assembly: ComVisible(false)]

namespace Peak.Cadder
{
    // Shell cloned from Peak.NextStep\AddIn.cs. The traps it documents
    // (registry casing, tab rebuild, icon paths, interop binding) apply here
    // unchanged. Only the identity, the command set and the callbacks differ.
    [ComVisible(true)]
    [Guid("5A19BED7-5BAE-4520-A820-99C7466C42AC")]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(ISwAddin))]
    public class AddIn : ISwAddin
    {
        public const string AddInTitle = "CADder Bridge";

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
        private static readonly string[] RetiredTitles = { "SW To Blender" };

        public const string AddInDescription =
            "Exports the assembly's kinematics as a rig manifest next to a "
            + "STEP file, for the CADder Blender add-on.";

        public static ISldWorks SwApp { get; private set; }

        /// <summary>
        /// The documents this session has put into a Blender. Refresh Model
        /// reads this only for an older Blender bridge. A current bridge
        /// lists in its registry file the documents its scenes hold
        /// (BlenderBridge.AnyHolding), which also holds after a restart of
        /// either program.
        /// </summary>
        private static readonly HashSet<string> Sent =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void RememberSent(string documentPath)
        {
            if (!string.IsNullOrEmpty(documentPath)) Sent.Add(documentPath);
        }

        public static bool WasSent(string documentPath)
        {
            return !string.IsNullOrEmpty(documentPath) && Sent.Contains(documentPath);
        }

        private int _cookie;
        private ICommandManager _cmdMgr;
        private CommandCallbacks _callbacks;

        /// <summary>
        /// The command manager of the running add-in, and the title of every
        /// command by its SolidWorks command id. The lab "ribbon" operation
        /// reads both to report what reached the ribbon. Nothing else uses
        /// them.
        /// </summary>
        internal static ICommandManager LabCommandManager { get; private set; }

        internal static readonly Dictionary<int, string> CommandTitles =
            new Dictionary<int, string>();

        /// <summary>The CommandGroup UserID. SolidWorks keeps it in the registry
        /// with the toolbar layout of the user, so it must never change.
        /// 71 is NEXT-STEP, 74 is this add-in.</summary>
        // 75 since 2026-09-15: the group went from six commands to four,
        // and SolidWorks has crashed at start on a group whose command set
        // shrank against its saved layout. A new id starts with no layout.
        private const int MainCmdGroupId = 75;
        private const int CmdExportUserId = 0;
        private const int CmdStepPlusUserId = 1;
        private const int CmdSendUserId = 2;
        private const int CmdOptionsUserId = 3;
        private const int CmdExportJsonUserId = 4;
        private const int CmdSendNativeUserId = 5;
        private const int CmdRefreshModelUserId = 6;

        // The interop types are EMBEDDED (see SolidWorksApi.props), so this
        // add-in has no SolidWorks assembly reference to satisfy: it loads on
        // any SolidWorks from 2022 up with nothing beside it. The
        // cross-version AssemblyResolve handler that used to live here, and
        // the interop copies it fell back on, went with it.

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
        private static IEnumerable<string> InstalledVersionKeys(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                if (!name.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase))
                    continue;
                var suffix = name.Substring("SOLIDWORKS ".Length).Trim();
                if (suffix.Length == 4 && suffix.All(char.IsDigit))
                    yield return name;
            }
        }

        /// <summary>
        /// The HKLM keys the add-in is registered under, from the subkey
        /// names of HKLM\SOFTWARE\SolidWorks.
        ///
        /// One key per SolidWorks year installed now, and the key that no
        /// year owns (SOFTWARE\SolidWorks\AddIns), which SolidWorks also
        /// reads. With the year keys alone, a SolidWorks year installed
        /// after setup did not list the add-in until setup ran again.
        /// </summary>
        internal static List<string> RegistrationKeys(
            IEnumerable<string> solidWorksSubKeys, string guid)
        {
            var keys = new List<string>();
            foreach (var ver in InstalledVersionKeys(solidWorksSubKeys))
                keys.Add($@"SOFTWARE\SolidWorks\{ver}\Addins\{guid}");
            keys.Add(AnyYearKey(guid));
            return keys;
        }

        private static string AnyYearKey(string guid)
        {
            return $@"SOFTWARE\SolidWorks\AddIns\{guid}";
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            var guid = t.GUID.ToString("B");
            string[] years;
            using (var swKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks"))
                years = swKey == null ? new string[0] : swKey.GetSubKeyNames();
            foreach (var path in RegistrationKeys(years, guid))
            {
                using (var key = Registry.LocalMachine.CreateSubKey(path))
                {
                    key.SetValue(null, 1);
                    key.SetValue("Title", AddInTitle + " " + AddInVersion);
                    key.SetValue("Description", AddInDescription);
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
                Registry.LocalMachine.DeleteSubKey(AnyYearKey(guid), throwOnMissingSubKey: false);
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

                // The return leg, so Blender can ask for geometry rather than
                // only be sent it. Started HERE because the marshalling
                // control it creates belongs to the thread that makes it, and
                // that has to be SolidWorks'. A failure to listen costs the
                // round trip and nothing else, so it never fails the connect.
                try
                {
                    Bridge.SwCommandServer.Start(
                        SwApp, Bridge.SwCommandHandler.Handle, Log);
                }
                catch (Exception ex) { Log("sw bridge start: " + ex.Message); }

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
            try { Bridge.SwCommandServer.Stop(Log); }
            catch (Exception ex) { Log("sw bridge stop: " + ex.Message); }
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
            // The group always holds all four commands: a group whose command
            // set shrinks against the saved layout crashed SolidWorks at
            // start (2026-09-15, twice). The advanced option decides only
            // what reaches the ribbon tab and the toolbar; the rest stay
            // menu items under Tools.
            var settings = AppSettings.Load(Log);
            bool advanced = settings.AdvancedCommands;
            var knownIds = new[] { CmdSendNativeUserId, CmdOptionsUserId,
                                   CmdRefreshModelUserId, CmdStepPlusUserId,
                                   CmdExportJsonUserId };
            bool ignorePrevious = hadPrevious && !SameIds(registryIds as int[], knownIds);

            // A saved layout belongs to the build it was saved against.
            // After an update, or a rebuild during development, SolidWorks
            // has drawn one of its OWN captions on a button of ours ("User
            // Defined Route" on Send to Blender, Oscar, 2026-09-17). So a
            // build this machine has not seen throws the layout away once.
            // It costs a user who moved the buttons that arrangement, which
            // is the same thing every version of the add-in costs anyway.
            string build = BuildStamp();
            if (build != settings.CommandUiBuild)
            {
                ignorePrevious = true;
                settings.CommandUiBuild = build;
                settings.Save(Log);
                Log("new build of the add-in: the ribbon is built again from "
                    + "nothing (" + build + ")");
            }

            var group = _cmdMgr.CreateCommandGroup2(
                MainCmdGroupId, AddInTitle, AddInDescription, "", -1, ignorePrevious, ref errors);
            if (group == null) { Log($"CreateCommandGroup2 failed ({errors})"); return; }

            ApplyIcons(group);

            // The image index is the command's position in the icon strip
            // (tools/Make-Icons.py builds it in this order, advanced or not).
            // An index past the strip breaks the artwork in silence.
            const int both = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            int rest = advanced ? both : (int)swCommandItemType_e.swMenuItem;

            group.AddCommandItem2(
                "Send to Blender", -1,
                "Send the assembly to a running Blender: geometry, appearances, "
                + "rig, parenting. One click, options behind Export Options",
                "Send to Blender", 0,
                nameof(CommandCallbacks.SendToBlenderNative),
                nameof(CommandCallbacks.EnableAnyDoc),
                CmdSendNativeUserId, both);

            group.AddCommandItem2(
                "Export Options", -1,
                "Persistent options: Blender import, rig pipeline, appearances, "
                + "which Blender to use",
                "Export Options", 1,
                nameof(CommandCallbacks.BlenderOptions),
                nameof(CommandCallbacks.EnableAlways),
                CmdOptionsUserId, both);

            group.AddCommandItem2(
                "Refresh Model", -1,
                "Bring the Blender scene up to date with this assembly: new "
                + "parts, deleted parts, the tree and the poses. What happens "
                + "to the rig is asked each time",
                "Refresh Model", 2,
                nameof(CommandCallbacks.RefreshModel),
                nameof(CommandCallbacks.EnableRefreshModel),
                CmdRefreshModelUserId, both);

            group.AddCommandItem2(
                "Export STEP+", -1,
                "Export STEP and keep the full appearance hierarchy (the NEXT-STEP engine)",
                "Export STEP+", 3,
                nameof(CommandCallbacks.ExportStepPlus),
                nameof(CommandCallbacks.EnableAnyDoc),
                CmdStepPlusUserId, rest);

            group.AddCommandItem2(
                "Export Rig", -1,
                "Export the rig manifest to disk, no STEP write and no Blender. "
                + "Occurrences are matched against the STEP file beside the "
                + "manifest when there is one",
                "Export Rig", 4,
                nameof(CommandCallbacks.ExportRigJson),
                nameof(CommandCallbacks.EnableExportRig),
                CmdExportJsonUserId, rest);

            group.HasToolbar = true;
            group.HasMenu = true;
            if (!group.Activate())
                Log("the command group did not activate: the ribbon may hold "
                    + "the wrong buttons");

            // Keep the command ids beside their titles, so the lab can say
            // which buttons the ribbon holds.
            LabCommandManager = _cmdMgr;
            CommandTitles.Clear();
            for (int i = 0; i < CommandOrder.Length; i++)
                CommandTitles[group.get_CommandID(i)] = CommandOrder[i];
            // The ids SolidWorks gave us. A button that draws somebody
            // else's caption is drawing somebody else's id, and this is
            // where that shows.
            Log("ribbon: " + string.Join(", ", CommandOrder.Select(
                (t, i) => t + " = " + group.get_CommandID(i)).ToArray()));

            // Both document types get the same buttons. Export Rig needs
            // mates, so EnableExportRig greys it on a part, and Refresh
            // Model greys until there is something to refresh. A grey
            // button is what SolidWorks does elsewhere, and a button that
            // disappears reads as a broken add-in (Oscar, 2026-09-16: "the
            // buttons are now missing", on a part).
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

                // get_CommandID takes the command's INDEX in the group, the
                // order CommandOrder lists. Passing the user ids here put
                // the wrong buttons on the ribbon (Oscar, 2026-09-15).
                // Without the advanced commands, Export STEP+ and Export Rig
                // are menu items only, so the tab cannot hold them.
                int[] indexes = advanced
                    ? new[] { 0, 1, 2, 3, 4 }
                    : new[] { 0, 1, 2 };
                var commandIds = indexes.Select(i => group.get_CommandID(i)).ToArray();
                var styles = indexes.Select(
                    _ => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray();
                bool added = box.AddCommands(commandIds, styles);
                if (!added) Log($"AddCommands failed for docType {docType}");
            }
        }

        /// <summary>
        /// The commands in the order AddCommandItem2 adds them, which is the
        /// order of the icon strip and the index that get_CommandID takes.
        /// </summary>
        internal static readonly string[] CommandOrder =
        {
            "Send to Blender", "Export Options", "Refresh Model",
            "Export STEP+", "Export Rig",
        };

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

                var commands = IconSizes.Select(s => Path.Combine(dir, "Cadder_" + s + ".png")).ToArray();
                var main = IconSizes.Select(s => Path.Combine(dir, $"CadderMain_{s}.png")).ToArray();

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

        /// <summary>When the running add-in file was written, which is
        /// what tells one build from the next.</summary>
        private static string BuildStamp()
        {
            try
            {
                string path = typeof(AddIn).Assembly.Location;
                return File.GetLastWriteTimeUtc(path).Ticks
                    .ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }

        private static bool SameIds(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ── Logging ─────────────────────────────────────────────────────────
        /// <summary>
        /// The log lives in the user's local app data, beside the bridge
        /// registry, never next to the DLL: an installed add-in sits in
        /// Program Files, where a user process cannot write.
        /// </summary>
        public static string LogPath
        {
            get
            {
                // The name needs its namespace. SolidWorks.Interop.sldworks
                // also declares an Environment type, so the short name is
                // ambiguous here.
                string dir = Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "PeakDesign", "CADder");
                return Path.Combine(dir, "cadder-debug.log");
            }
        }

        /// <summary>Past this size the log is renamed to .1 (replacing the
        /// previous .1) and a fresh one starts, so a machine that exports
        /// every day for a year does not grow a gigabyte of text.</summary>
        private const long LogRotateBytes = 8L * 1024 * 1024;

        public static void Log(string message)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var info = new FileInfo(path);
                if (info.Exists && info.Length > LogRotateBytes)
                {
                    string older = path + ".1";
                    if (File.Exists(older)) File.Delete(older);
                    File.Move(path, older);
                }
                File.AppendAllText(path,
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

        public void ExportRig() => Guard("Export Rig", () => ExportCommand.Run(AddIn.SwApp));
        public void ExportRigJson() => Guard("Export Rig", () => ExportCommand.RunManifestOnly(AddIn.SwApp));
        public void ExportStepPlus() => Guard("Export STEP+", () => StepPlusCommand.Run(AddIn.SwApp));
        public void SendToBlender() => Guard("Send to Blender", () => SendToBlenderCommand.Run(AddIn.SwApp));
        public void SendToBlenderNative() => Guard("Send to Blender", () => SendToBlenderCommand.Run(AddIn.SwApp, native: true));
        public void BlenderOptions() => Guard("Export Options", () => BlenderOptionsDialog.Run(AddIn.SwApp));
        public void RefreshModel() => Guard("Refresh Model", () => RefreshModelCommand.Run(AddIn.SwApp));

        /// <summary>
        /// Runs one ribbon command. SolidWorks calls these through late
        /// binding, and an exception that leaves a callback ends SolidWorks,
        /// so it stops here: it goes to the log and to the user. Last, the
        /// command frees the SolidWorks objects it read (ComRelease), while
        /// every document it read is still open.
        /// </summary>
        private static void Guard(string name, Action command)
        {
            try { command(); }
            catch (Exception ex)
            {
                AddIn.Log(name + " failed: " + ex);
                try
                {
                    AddIn.SwApp?.SendMsgToUser2(name + " failed: " + ex.Message,
                        (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
                }
                catch { }
            }
            finally { Sw.ComRelease.Flush(); }
        }

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

        /// <summary>
        /// Refresh Model is offered once there is something to refresh: a
        /// running Blender with the bridge holds a scene of this document.
        /// Before that the button would only ever answer "send it first",
        /// and a button that cannot work should say so by being grey
        /// (Oscar, 2026-09-16). A Blender that crashed or closed takes the
        /// button with it (Oscar, 2026-09-23), and one that opens the saved
        /// scene again brings it back.
        /// </summary>
        public int EnableRefreshModel()
        {
            var doc = AddIn.SwApp?.ActiveDoc as IModelDoc2;
            if (doc == null) return 0;
            int type = doc.GetType();
            if (type != (int)swDocumentTypes_e.swDocASSEMBLY
                && type != (int)swDocumentTypes_e.swDocPART) return 0;
            string path = doc.GetPathName();
            return Bridge.BlenderBridge.AnyHolding(path, AddIn.WasSent(path)) ? 1 : 0;
        }

        public int EnableAlways() => 1;
    }
}
