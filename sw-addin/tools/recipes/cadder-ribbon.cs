public sealed partial class SwMcpScript
{
    /// <summary>What the CADder Bridge put on the ribbon, read back from SolidWorks: the tab and its buttons for each document type, the command ids, and whether Refresh Model is enabled for the active document.</summary>
    private object CadderRibbon()
    {
        // SolidWorks gives the object of the callbacks (SetAddinCallbackInfo2),
        // and its Owner is the add-in.
        var callbackObject = swApp.GetAddInObject(Cadder.AddInClsid);
        if (callbackObject == null) throw new InvalidOperationException("SolidWorks has no CADder Bridge add-in object. Is the add-in on?");
        var addin = callbackObject.GetType().Name == "CommandCallbacks" ? Cadder.Get(callbackObject, "Owner") : callbackObject;
        if (addin == null) throw new InvalidOperationException("The callbacks of the CADder Bridge have no Owner.");
        var manager = Cadder.Get(addin, "_cmdMgr") as CommandManager;
        if (manager == null) return new { ok = false, error = "the add-in built no command UI" };
        string title = (string)Cadder.Static("AddIn", "AddInTitle");
        var order = (string[])Cadder.Static("AddIn", "CommandOrder");
        var group = manager.GetCommandGroup((int)Cadder.Static("AddIn", "MainCmdGroupId"));
        var titles = new Dictionary<int, string>();
        if (group != null)
            for (int i = 0; i < order.Length; i++) titles[group.get_CommandID(i)] = order[i];

        var tabs = new List<object>();
        foreach (var docType in new[] { swDocumentTypes_e.swDocASSEMBLY, swDocumentTypes_e.swDocPART })
        {
            var buttons = new List<object>();
            var tab = manager.GetCommandTab((int)docType, title);
            if (tab != null)
            {
                foreach (var raw in (tab.CommandTabBoxes() as object[]) ?? new object[0])
                {
                    var box = raw as CommandTabBox;
                    if (box == null) continue;
                    object ids, styles;
                    box.GetCommands(out ids, out styles);
                    foreach (int id in (ids as int[]) ?? new int[0])
                    {
                        string name;
                        buttons.Add(titles.TryGetValue(id, out name) ? name : "command " + id);
                    }
                }
            }
            tabs.Add(new { document = docType == swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "part", tab = tab != null, buttons });
        }
        var settings = Cadder.Call("Core.AppSettings", "Load", Cadder.Log, null);
        var callbacks = Cadder.New("CommandCallbacks");
        return new
        {
            ok = true,
            advanced = Cadder.Get(settings, "AdvancedCommands"),
            commands = order,
            ids = titles.OrderBy(kv => kv.Key).Select(kv => kv.Value + " = " + kv.Key).ToList(),
            tabs,
            // 1 enabled, 0 grey: the callback that SolidWorks itself calls.
            refreshModelEnabled = Cadder.CallOn(callbacks, "EnableRefreshModel"),
        };
    }

    /// <summary>
    /// Clicks a ribbon button: runs the callback that SolidWorks calls for it,
    /// with all its dialogs. "send" is Send to Blender (direct link), "refresh"
    /// is Refresh Model, or give a callback name (SendToBlender, ExportRig,
    /// ExportRigJson, ExportStepPlus, BlenderOptions). Use a small wait_s, then
    /// dialog to read and press each dialog box, then job for the result.
    /// </summary>
    private object CadderClick(string command)
    {
        string callback;
        switch ((command ?? "").Trim().ToLowerInvariant())
        {
            case "send": callback = "SendToBlenderNative"; break;
            case "refresh": callback = "RefreshModel"; break;
            default: callback = (command ?? "").Trim(); break;
        }
        long mark = Cadder.LogMark();
        var callbacks = Cadder.New("CommandCallbacks");
        Cadder.CallOn(callbacks, callback);
        return new { command = callback, log = Cadder.LogSince(mark) };
    }

    /// <summary>
    /// Runs the progress bar of the export through its stages with no export,
    /// and returns what SolidWorks answered. The bar draws in the status bar,
    /// which view cannot show, so this is how the bar is checked.
    /// </summary>
    private object CadderProgressCheck(int steps = 20, int holdMs = 40)
    {
        steps = Math.Max(1, steps);
        var bar = Cadder.Call("Sw.SwProgressBar", "Open", swApp, "Progress bar check", Cadder.Log);
        if (bar == null || bar.GetType().Name != "SwProgressBar") return new { ok = false, error = "SolidWorks gave no progress bar" };
        try
        {
            Cadder.CallOn(bar, "Stage", "Reading the assembly", 0, 8, 0);
            Cadder.CallOn(bar, "Stage", "Measuring the freedom of " + steps + " pair(s)", 24, 58, steps);
            for (int i = 1; i <= steps; i++)
            {
                Cadder.CallOn(bar, "Step", i);
                if (holdMs > 0) System.Threading.Thread.Sleep(holdMs);
            }
            Cadder.CallOn(bar, "Stage", "Writing the manifest", 95, 100, 0);
            Cadder.CallOn(bar, "Step", 1);
        }
        finally { ((IDisposable)bar).Dispose(); }
        return new
        {
            ok = true,
            updates = Cadder.Get(bar, "Updates"),
            lastAnswer = Cadder.Get(bar, "LastAnswer"),
            cancelled = Cadder.Get(bar, "Cancelled"),
        };
    }
}
